using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.Services.Clients;
using Reply = Balenthiran.SecretProvisioner.Tests.FakeApiServer.Reply;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// Ported from <c>src/kube.test.js</c> (the client half; configuration is in <see cref="InClusterConfigTests"/>),
/// then made create-only (secret-provisioner#9). Every case drives <c>KubernetesClient</c> through a
/// fake handler and compares the request to a literal.
/// </summary>
/// <remarks>
/// The literals are deliberately spelled out rather than built from the code under test: a test that
/// derives the expected path from the client passes for a path of <c>/undefined</c>.
/// </remarks>
public class KubernetesClientTests
{
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.a-real-looking-service-account-token.signature";

    private static KubernetesClient ClientOver(FakeApiServer handler) => new(handler.Client(), Token);

    private static Reply Ok(int status, object body) => FakeApiServer.Ok(status, body);

    private static Reply ApiStatus(int status, string message, string reason) => FakeApiServer.ApiStatus(status, message, reason);

    // What a dry-run create of a free name answers: 201 and the echo of the empty Secret sent.
    private static Reply Free() => Ok(201, new { kind = "Secret", apiVersion = "v1", metadata = new { name = "x" }, type = "Opaque" });

    private static Reply Taken(string name = "x") => ApiStatus(409, $"secrets \"{name}\" already exists", "AlreadyExists");

    private static async Task<Abstractions.DataModels.ISecretProbe> ProbeOne(FakeApiServer handler, string ns = "balenthiran", string name = "balenthiran-secrets") =>
        Assert.Single(await ClientOver(handler).ProbeSecretsAsync([(ns, name)]));

    // ─── the probe: "does it exist?" with nothing but create ────────────────

    [Fact]
    public async Task A_probe_is_a_dry_run_POST_to_the_collection_carrying_no_data()
    {
        var handler = new FakeApiServer(Free());
        var probe = await ProbeOne(handler);

        var sent = Assert.Single(handler.Requests);
        // POST, with dryRun=All. Without the query parameter this is a REAL create of an empty
        // Secret, which would then shadow the real one forever: create-only cannot fix it.
        Assert.Equal("POST", sent.Method);
        Assert.Equal("https://10.96.0.1/api/v1/namespaces/balenthiran/secrets?dryRun=All", sent.Uri);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal($"Bearer {Token}", sent.Authorization);
        Assert.Equal("application/json", sent.Accept);

        using var body = JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        Assert.Equal("Secret", root.GetProperty("kind").GetString());
        Assert.Equal("balenthiran-secrets", root.GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal("balenthiran", root.GetProperty("metadata").GetProperty("namespace").GetString());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.False(root.TryGetProperty("stringData", out _));

        Assert.Equal(false, probe.Exists);
        Assert.Null(probe.Error);
    }

    [Fact]
    public async Task A_409_AlreadyExists_means_the_Secret_exists()
    {
        var probe = await ProbeOne(new FakeApiServer(Taken("balenthiran-secrets")));
        Assert.Equal(true, probe.Exists);
        Assert.Null(probe.Error);
    }

    [Fact]
    public async Task A_409_for_any_other_reason_is_a_failure_not_an_answer()
    {
        // "Conflict" is not "AlreadyExists". Reading every 409 as "exists" would hide a Secret that
        // was never made behind an unrelated error.
        var probe = await ProbeOne(new FakeApiServer(ApiStatus(409, "Operation cannot be fulfilled", "Conflict")));
        Assert.Null(probe.Exists);
        Assert.Contains("409", probe.Error);
    }

    [Fact]
    public async Task A_403_is_a_failure_not_an_absence()
    {
        // A namespace outside targetNamespaces answers 403. Reading that as "absent" plans a real
        // create the API would refuse anyway — but reading a 503 the same way would not be refused.
        var probe = await ProbeOne(new FakeApiServer(ApiStatus(403, "secrets is forbidden", "Forbidden")));
        Assert.Null(probe.Exists);
        Assert.Contains("secrets is forbidden", probe.Error);
    }

    [Fact]
    public async Task A_failed_probe_carries_an_error_and_NO_answer_and_the_rest_still_run()
    {
        // This is the property the observer depends on. A transient 503 that came back as
        // Exists = false would read as "free name", and the planner would plan a create over a
        // Secret that may well exist.
        var handler = new FakeApiServer(
            Free(),
            ApiStatus(503, "the server is currently unable to handle the request", "ServiceUnavailable"),
            Taken());
        var results = await ClientOver(handler).ProbeSecretsAsync([("a", "one"), ("a", "two"), ("b", "three")]);

        Assert.Equal(["one", "two", "three"], results.Select(r => r.Name));
        Assert.Equal(false, results[0].Exists);
        Assert.Null(results[1].Exists);
        Assert.Contains("503", results[1].Error);
        Assert.Contains("the server is currently unable to handle the request", results[1].Error);
        Assert.Equal(true, results[2].Exists);
        Assert.True(handler.Drained);
    }

    [Fact]
    public async Task A_transport_level_failure_is_a_probe_failure_too_not_a_crash()
    {
        var probe = await ProbeOne(new FakeApiServer(new HttpRequestException("getaddrinfo ENOTFOUND kubernetes.default.svc")));
        Assert.Contains("ENOTFOUND", probe.Error);
        Assert.Null(probe.Exists);
    }

    [Fact]
    public async Task Secrets_are_probed_one_at_a_time_in_order()
    {
        var handler = new FakeApiServer(Free(), Free(), Free());
        await ClientOver(handler).ProbeSecretsAsync([("a", "x"), ("b", "y"), ("c", "z")]);
        Assert.Equal(
            ["https://10.96.0.1/api/v1/namespaces/a/secrets?dryRun=All", "https://10.96.0.1/api/v1/namespaces/b/secrets?dryRun=All", "https://10.96.0.1/api/v1/namespaces/c/secrets?dryRun=All"],
            handler.Requests.Select(r => r.Uri));
        Assert.Equal(["y"], handler.Requests.Skip(1).Take(1).Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("metadata").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task A_namespace_name_is_encoded_into_the_path_never_concatenated()
    {
        var handler = new FakeApiServer(Free());
        await ClientOver(handler).ProbeSecretsAsync([("a b/../../nodes", "x")]);
        Assert.Equal("https://10.96.0.1/api/v1/namespaces/a%20b%2F..%2F..%2Fnodes/secrets?dryRun=All", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_reported_as_a_probe_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeApiServer(Free());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientOver(handler).ProbeSecretsAsync([("a", "x")], cts.Token));
    }

    // ─── create ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSecret_POSTs_to_the_collection_with_stringData()
    {
        var handler = new FakeApiServer(Ok(201, new { kind = "Secret" }));
        await ClientOver(handler).CreateSecretAsync("kit", "kit-auth", new Dictionary<string, string> { ["password"] = new string('a', 64) });

        var sent = Assert.Single(handler.Requests);
        // POST to the COLLECTION, and NOT a dry run. A PUT to the item path is an unconditional
        // replace, which would discard every key not in `data` — and the 409 that makes create-only
        // a guarantee would never be returned.
        Assert.Equal("POST", sent.Method);
        Assert.Equal("https://10.96.0.1/api/v1/namespaces/kit/secrets", sent.Uri);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal($"Bearer {Token}", sent.Authorization);
        Assert.Equal("application/json", sent.Accept);

        using var body = JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        Assert.Equal("v1", root.GetProperty("apiVersion").GetString());
        Assert.Equal("Secret", root.GetProperty("kind").GetString());
        Assert.Equal("Opaque", root.GetProperty("type").GetString());
        Assert.Equal("kit-auth", root.GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal("kit", root.GetProperty("metadata").GetProperty("namespace").GetString());
        // `stringData`, so nothing in this repo ever base64-encodes a value.
        Assert.Equal(new string('a', 64), root.GetProperty("stringData").GetProperty("password").GetString());
        Assert.False(root.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Content_length_is_the_byte_length_not_the_character_count()
    {
        var handler = new FakeApiServer(Ok(201, new { }));
        // A name a human would actually type into a declaration one day.
        await ClientOver(handler).CreateSecretAsync("kit", "café-secrets", new Dictionary<string, string> { ["k"] = "v" });

        var sent = Assert.Single(handler.Requests);
        Assert.Contains("café-secrets", sent.Body);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(sent.Body!), sent.ContentLength);
        Assert.Equal(sent.ByteCount, sent.ContentLength);
        Assert.NotEqual(sent.Body!.Length, sent.ContentLength);
    }

    [Fact]
    public async Task A_409_from_create_surfaces_as_a_Conflict_the_caller_can_branch_on()
    {
        var handler = new FakeApiServer(ApiStatus(409, "secrets \"kit-auth\" already exists", "AlreadyExists"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(
            () => ClientOver(handler).CreateSecretAsync("kit", "kit-auth", new Dictionary<string, string> { ["password"] = "x" }));
        Assert.Equal(409, error.Status);
        Assert.Equal("AlreadyExists", error.Reason);
        Assert.Equal("creating Secret kit/kit-auth: the Kubernetes API answered 409 — secrets \"kit-auth\" already exists", error.Message);
    }

    [Fact]
    public async Task A_create_that_fails_sends_exactly_one_write_and_does_not_retry()
    {
        var handler = new FakeApiServer(Taken("kit-auth"));
        await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).CreateSecretAsync("kit", "kit-auth", new Dictionary<string, string> { ["k"] = "v" }));
        Assert.Single(handler.Requests);
    }

    // ─── what may be quoted, and what may never be ──────────────────────────

    [Fact]
    public async Task The_service_account_token_never_reaches_an_error_message()
    {
        // Transport errors are known to carry request details, and the token is in a header. So the
        // redaction is applied to the transport failure too, not only to bodies we assemble ourselves.
        var handler = new FakeApiServer(new HttpRequestException($"write EPROTO; request was {{\"headers\":{{\"authorization\":\"Bearer {Token}\"}}}}"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).CreateSecretAsync("kit", "s", new Dictionary<string, string>()));
        Assert.DoesNotContain(Token, error.Message);
        Assert.Contains("<redacted>", error.Message);
        Assert.Null(error.Status);
    }

    [Fact]
    public async Task A_token_echoed_back_by_the_server_is_redacted_from_the_quoted_body()
    {
        // A fake server that reflects the credential it was sent, in a Status message and in a
        // body that is not JSON at all.
        var handler = new FakeApiServer(
            (Func<FakeApiServer.Sent, Reply>)(sent => ApiStatus(401, $"Unauthorized: {sent.Authorization}", "Unauthorized")),
            (Func<FakeApiServer.Sent, Reply>)(sent => new Reply(502, $"<html>bad gateway for {sent.Authorization}</html>")));
        var client = ClientOver(handler);

        foreach (var _ in new[] { 401, 502 })
        {
            var error = await Assert.ThrowsAsync<KubernetesApiException>(() => client.CreateSecretAsync("kit", "s", new Dictionary<string, string>()));
            Assert.DoesNotContain(Token, error.Message);
            Assert.Contains("Bearer <redacted>", error.Message);
        }
    }

    [Fact]
    public async Task A_token_in_a_transport_failure_is_redacted_from_a_probe_error_too()
    {
        var probe = await ProbeOne(new FakeApiServer(new HttpRequestException($"write EPROTO; Bearer {Token}")));
        Assert.DoesNotContain(Token, probe.Error);
        Assert.Contains("<redacted>", probe.Error);
    }

    [Fact]
    public async Task A_non_2xx_body_IS_quoted_because_that_is_the_only_diagnosis_anyone_gets()
    {
        // The opposite rule to the tests above, and it has to be asserted too: "redact everything"
        // would be trivially safe and would make every failure read as `the API answered 403`.
        var handler = new FakeApiServer(ApiStatus(403, "secrets is forbidden: User \"system:serviceaccount:balenthiran:secret-provisioner\" cannot create resource \"secrets\"", "Forbidden"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).CreateSecretAsync("kit", "s", new Dictionary<string, string>()));
        Assert.Contains("cannot create resource \"secrets\"", error.Message);
        Assert.Equal(403, error.Status);
        Assert.Equal("Forbidden", error.Reason);
    }

    [Fact]
    public async Task A_non_JSON_error_body_is_quoted_for_its_first_500_characters()
    {
        var handler = new FakeApiServer(new Reply(502, new string('x', 400) + new string('y', 400)));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).CreateSecretAsync("kit", "s", new Dictionary<string, string>()));
        Assert.EndsWith(new string('x', 400) + new string('y', 100), error.Message);
        Assert.DoesNotContain(new string('y', 101), error.Message);
        Assert.Equal(502, error.Status);
        Assert.Null(error.Reason);
    }

    [Fact]
    public async Task Any_non_2xx_is_a_failure()
    {
        // 3xx is not a success either: the status is checked against the 2xx range, not "at least 200".
        var handler = new FakeApiServer(new Reply(301, "moved"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).CreateSecretAsync("kit", "s", new Dictionary<string, string>()));
        Assert.Equal(301, error.Status);
    }

    [Fact]
    public async Task A_3xx_on_a_probe_is_a_failure_not_a_free_name()
    {
        var probe = await ProbeOne(new FakeApiServer(new Reply(301, "moved")));
        Assert.Null(probe.Exists);
        Assert.Contains("301", probe.Error);
    }

    [Fact]
    public void The_client_satisfies_the_interface_the_reconciler_and_executor_call()
    {
        // Both call it by interface; a rename that looks harmless here is a failed reconcile there.
        Assert.IsAssignableFrom<IKubernetesClient>(ClientOver(new FakeApiServer()));
    }
}
