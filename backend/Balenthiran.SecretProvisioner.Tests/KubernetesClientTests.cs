using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.Database;
using Balenthiran.SecretProvisioner.Services;
using Reply = Balenthiran.SecretProvisioner.Tests.FakeApiServer.Reply;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// Port of <c>src/kube.test.js</c> (the client half; configuration is in <see cref="InClusterConfigTests"/>).
/// Every case drives <c>KubernetesClient</c> through a fake handler and compares the request to a literal.
/// </summary>
/// <remarks>
/// The literals are deliberately spelled out rather than built from the code under test: a test that
/// derives the expected path from the client passes for a path of <c>/undefined</c>.
/// </remarks>
public class KubernetesClientTests
{
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.a-real-looking-service-account-token.signature";

    private static KubernetesClient ClientOver(FakeApiServer handler) => new(handler.Client(), Token, new Observer());

    private static Reply Ok(int status, object body) => FakeApiServer.Ok(status, body);

    private static Reply ApiStatus(int status, string message, string reason) => FakeApiServer.ApiStatus(status, message, reason);

    private static Reply EmptyList() => Ok(200, new { kind = "SecretList", items = Array.Empty<object>() });

    // A Secret exactly as the API returns one on a 200: values present, base64, in `data`, beside the names.
    private static readonly Dictionary<string, string> LiveValues = new()
    {
        ["Jwt__Secret"] = "ZGVhZGJlZWZkZWFkYmVlZg==",
        ["Admin__Key"] = "YWRtaW4ta2V5",
    };

    private static object LiveSecret(string? resourceVersion = "80421") => new
    {
        kind = "Secret",
        apiVersion = "v1",
        metadata = new { name = "balenthiran-secrets", @namespace = "balenthiran", resourceVersion },
        type = "Opaque",
        data = LiveValues,
    };

    private static Reply LiveList() => Ok(200, new { kind = "SecretList", items = new[] { LiveSecret() } });

    // ─── listing, and the one property the observer depends on ──────────────

    [Fact]
    public async Task ListSecrets_asks_the_right_URL_with_the_right_credential()
    {
        var handler = new FakeApiServer(LiveList());
        var listing = await ClientOver(handler).ListSecretsAsync("balenthiran");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("GET", sent.Method);
        Assert.Equal("https://10.96.0.1/api/v1/namespaces/balenthiran/secrets", sent.Uri);
        Assert.Equal($"Bearer {Token}", sent.Authorization);
        Assert.Equal("application/json", sent.Accept);
        Assert.Null(sent.Body);
        Assert.Single(listing.Items!);
    }

    [Fact]
    public async Task A_namespace_name_is_encoded_into_the_path_never_concatenated()
    {
        var handler = new FakeApiServer(EmptyList());
        await ClientOver(handler).ListSecretsAsync("a b/../../nodes");
        Assert.Equal("https://10.96.0.1/api/v1/namespaces/a%20b%2F..%2F..%2Fnodes/secrets", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task A_200_that_is_not_a_SecretList_is_a_failure_not_an_empty_namespace()
    {
        // The realistic source is a proxy or a mesh sidecar answering 200 with HTML.
        var handler = new FakeApiServer(new Reply(200, "<html>Gateway</html>"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).ListSecretsAsync("balenthiran"));
        Assert.Contains("is not a v1.SecretList", error.Message);
    }

    [Fact]
    public async Task A_200_without_items_is_not_a_SecretList_and_is_not_quoted()
    {
        var handler = new FakeApiServer(new Reply(200, """{"kind":"Status","note":"SENTINEL-2XX-BODY"}"""));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).ListSecretsAsync("balenthiran"));
        Assert.Contains("is not a v1.SecretList", error.Message);
        Assert.DoesNotContain("SENTINEL-2XX-BODY", error.Message);
    }

    [Fact]
    public async Task A_failed_namespace_carries_an_error_and_NO_listing()
    {
        // This is the property the entire service is arranged around. The observer branches on
        // `Error is not null`, and an empty listing here would make a transient 503 read as "this
        // namespace holds no Secrets" — after which the planner plans a create for a Secret that
        // already exists.
        var handler = new FakeApiServer(
            EmptyList(),
            ApiStatus(503, "the server is currently unable to handle the request", "ServiceUnavailable"),
            LiveList());
        var results = await ClientOver(handler).ObserveNamespacesAsync(["empty-ns", "unreadable-ns", "balenthiran"]);

        Assert.Equal(3, results.Count);
        Assert.Equal(["empty-ns", "unreadable-ns", "balenthiran"], results.Select(r => r.Namespace));

        Assert.Null(results[0].Error);
        Assert.Empty(results[0].Listing!.Items!);

        Assert.Null(results[1].Listing);
        Assert.Contains("503", results[1].Error);
        Assert.Contains("the server is currently unable to handle the request", results[1].Error);

        Assert.Single(results[2].Listing!.Items!);
        Assert.True(handler.Drained);
    }

    [Fact]
    public async Task One_namespace_failing_does_not_stop_the_others_being_read()
    {
        var handler = new FakeApiServer(ApiStatus(403, "secrets is forbidden", "Forbidden"), EmptyList());
        var results = await ClientOver(handler).ObserveNamespacesAsync(["forbidden-ns", "fine-ns"]);
        Assert.NotNull(results[0].Error);
        Assert.Null(results[1].Error);
        Assert.NotNull(results[1].Listing);
    }

    [Fact]
    public async Task A_transport_level_failure_is_a_namespace_failure_too_not_a_crash()
    {
        var handler = new FakeApiServer(new HttpRequestException("getaddrinfo ENOTFOUND kubernetes.default.svc"));
        var result = Assert.Single(await ClientOver(handler).ObserveNamespacesAsync(["balenthiran"]));
        Assert.Contains("ENOTFOUND", result.Error);
        Assert.Null(result.Listing);
    }

    [Fact]
    public async Task A_200_that_is_not_a_SecretList_is_a_namespace_failure_not_an_empty_listing()
    {
        var handler = new FakeApiServer(new Reply(200, "<html>Gateway</html>"));
        var result = Assert.Single(await ClientOver(handler).ObserveNamespacesAsync(["balenthiran"]));
        Assert.Null(result.Listing);
        Assert.Contains("is not a v1.SecretList", result.Error);
    }

    [Fact]
    public async Task Namespaces_are_listed_one_at_a_time_in_order()
    {
        var handler = new FakeApiServer(EmptyList(), EmptyList(), EmptyList());
        await ClientOver(handler).ObserveNamespacesAsync(["a", "b", "c"]);
        Assert.Equal(
            ["https://10.96.0.1/api/v1/namespaces/a/secrets", "https://10.96.0.1/api/v1/namespaces/b/secrets", "https://10.96.0.1/api/v1/namespaces/c/secrets"],
            handler.Requests.Select(r => r.Uri));
    }

    // ─── create-only ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSecret_POSTs_to_the_collection_with_stringData()
    {
        var handler = new FakeApiServer(Ok(201, new { kind = "Secret" }));
        await ClientOver(handler).CreateSecretAsync("kit", "kit-auth", new Dictionary<string, string> { ["password"] = new string('a', 64) });

        var sent = Assert.Single(handler.Requests);
        // POST to the COLLECTION. A PUT to the item path is an unconditional replace, which would
        // discard every key not in `data` — and the 409 that makes create-only a guarantee would
        // never be returned.
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
        Assert.Contains("already exists", error.Message);
        Assert.Equal("creating Secret kit/kit-auth: the Kubernetes API answered 409 — secrets \"kit-auth\" already exists", error.Message);
    }

    // ─── the patch path, and its compare-and-swap ───────────────────────────

    [Fact]
    public async Task PatchSecretKey_reads_then_sends_a_conditional_merge_patch_of_one_key()
    {
        var handler = new FakeApiServer(Ok(200, LiveSecret("80421")), Ok(200, new { kind = "Secret" }));
        await ClientOver(handler).PatchSecretKeyAsync("balenthiran", "balenthiran-secrets", "DATABASE_URL", "Host=pg;Port=5432");

        var read = handler.Requests[0];
        var patch = handler.Requests[1];
        Assert.Equal("GET", read.Method);
        Assert.Equal("https://10.96.0.1/api/v1/namespaces/balenthiran/secrets/balenthiran-secrets", read.Uri);

        Assert.Equal("PATCH", patch.Method);
        Assert.Equal("https://10.96.0.1/api/v1/namespaces/balenthiran/secrets/balenthiran-secrets", patch.Uri);
        Assert.Equal("application/merge-patch+json", patch.ContentType);
        Assert.Equal($"Bearer {Token}", patch.Authorization);

        using var body = JsonDocument.Parse(patch.Body!);
        var root = body.RootElement;
        // The precondition. Without it the check above becomes a plain check-then-act race and
        // this method stops being create-only.
        Assert.Equal("80421", root.GetProperty("metadata").GetProperty("resourceVersion").GetString());
        Assert.Equal("Host=pg;Port=5432", root.GetProperty("stringData").GetProperty("DATABASE_URL").GetString());
        Assert.Single(root.GetProperty("stringData").EnumerateObject());
        // The existing keys are neither re-sent nor mentioned.
        Assert.False(root.TryGetProperty("data", out _));
        Assert.DoesNotContain("Jwt__Secret", patch.Body);
        Assert.DoesNotContain(LiveValues["Jwt__Secret"], patch.Body);
    }

    [Fact]
    public async Task The_name_of_a_patched_Secret_is_encoded_into_the_path()
    {
        var handler = new FakeApiServer(Ok(200, LiveSecret()), Ok(200, new { }));
        await ClientOver(handler).PatchSecretKeyAsync("balenthiran", "a b/c", "K", "v");
        Assert.All(handler.Requests, r => Assert.Equal("https://10.96.0.1/api/v1/namespaces/balenthiran/secrets/a%20b%2Fc", r.Uri));
    }

    [Fact]
    public async Task An_existing_key_is_refused_this_provisioner_never_overwrites()
    {
        var handler = new FakeApiServer(Ok(200, LiveSecret()));
        var error = await Assert.ThrowsAsync<SecretWriteRefusedException>(
            () => ClientOver(handler).PatchSecretKeyAsync("balenthiran", "balenthiran-secrets", "Jwt__Secret", "new"));
        Assert.Contains("already present", error.Message);
        // And, the half that matters: it never got as far as writing.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_key_present_only_in_stringData_is_also_refused()
    {
        var secret = new { metadata = new { resourceVersion = "1" }, stringData = new Dictionary<string, string> { ["K"] = "v" } };
        var handler = new FakeApiServer(Ok(200, secret));
        await Assert.ThrowsAsync<SecretWriteRefusedException>(() => ClientOver(handler).PatchSecretKeyAsync("n", "s", "K", "new"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_Secret_returned_without_a_resourceVersion_is_refused_not_patched_unconditionally()
    {
        var handler = new FakeApiServer(Ok(200, LiveSecret(resourceVersion: null)));
        var error = await Assert.ThrowsAsync<SecretWriteRefusedException>(
            () => ClientOver(handler).PatchSecretKeyAsync("balenthiran", "balenthiran-secrets", "DATABASE_URL", "v"));
        Assert.Contains("could not be made conditional", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_Secret_read_that_is_not_JSON_is_refused_not_patched()
    {
        var handler = new FakeApiServer(new Reply(200, "<html/>"));
        await Assert.ThrowsAsync<SecretWriteRefusedException>(() => ClientOver(handler).PatchSecretKeyAsync("n", "s", "K", "v"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_409_on_the_patch_is_the_compare_and_swap_doing_its_job()
    {
        var handler = new FakeApiServer(
            Ok(200, LiveSecret("80421")),
            ApiStatus(409, "Operation cannot be fulfilled on secrets \"balenthiran-secrets\": the object has been modified", "Conflict"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(
            () => ClientOver(handler).PatchSecretKeyAsync("balenthiran", "balenthiran-secrets", "DATABASE_URL", "v"));
        Assert.Equal(409, error.Status);
        Assert.Equal("Conflict", error.Reason);
    }

    // ─── what may be quoted, and what may never be ──────────────────────────

    [Fact]
    public async Task No_value_from_a_200_response_reaches_an_error_message()
    {
        // The patch path is the only place this client reads a 200 containing values. Every way out
        // of it — refusal, API failure, transport failure — is checked against every value in the
        // fixture, because it takes exactly one serialised Secret in a future error message to undo it.
        Func<FakeApiServer>[] attempts =
        [
            () => new FakeApiServer(Ok(200, LiveSecret())),
            () => new FakeApiServer(Ok(200, LiveSecret()), ApiStatus(409, "the object has been modified", "Conflict")),
            () => new FakeApiServer(Ok(200, LiveSecret()), new HttpRequestException("socket hang up")),
        ];
        var keys = new[] { "Jwt__Secret", "DATABASE_URL", "DATABASE_URL" };

        var checkedCount = 0;
        for (var i = 0; i < attempts.Length; i++)
        {
            var client = ClientOver(attempts[i]());
            var error = await Assert.ThrowsAnyAsync<AppException>(() => client.PatchSecretKeyAsync("balenthiran", "balenthiran-secrets", keys[i], "v"));
            foreach (var value in LiveValues.Values)
            {
                Assert.DoesNotContain(value, error.Message);
            }

            checkedCount++;
        }

        Assert.Equal(attempts.Length, checkedCount);
        Assert.All(LiveValues.Values, v => Assert.NotEmpty(v));
    }

    [Fact]
    public async Task The_service_account_token_never_reaches_an_error_message()
    {
        // Transport errors are known to carry request details, and the token is in a header. So the
        // redaction is applied to the transport failure too, not only to bodies we assemble ourselves.
        var handler = new FakeApiServer(new HttpRequestException($"write EPROTO; request was {{\"headers\":{{\"authorization\":\"Bearer {Token}\"}}}}"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).ListSecretsAsync("balenthiran"));
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
            var error = await Assert.ThrowsAsync<KubernetesApiException>(() => client.ListSecretsAsync("balenthiran"));
            Assert.DoesNotContain(Token, error.Message);
            Assert.Contains("Bearer <redacted>", error.Message);
        }
    }

    [Fact]
    public async Task A_non_2xx_body_IS_quoted_because_that_is_the_only_diagnosis_anyone_gets()
    {
        // The opposite rule to the tests above, and it has to be asserted too: "redact everything"
        // would be trivially safe and would make every failure read as `the API answered 403`.
        var handler = new FakeApiServer(ApiStatus(403, "secrets is forbidden: User \"system:serviceaccount:secrets:provisioner\" cannot list resource \"secrets\"", "Forbidden"));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).ListSecretsAsync("balenthiran"));
        Assert.Contains("cannot list resource \"secrets\"", error.Message);
        Assert.Equal(403, error.Status);
        Assert.Equal("Forbidden", error.Reason);
    }

    [Fact]
    public async Task A_non_JSON_error_body_is_quoted_for_its_first_500_characters()
    {
        var handler = new FakeApiServer(new Reply(502, new string('x', 400) + new string('y', 400)));
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).ListSecretsAsync("balenthiran"));
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
        var error = await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).ListSecretsAsync("balenthiran"));
        Assert.Equal(301, error.Status);
    }

    [Fact]
    public void The_client_satisfies_the_interface_the_executor_calls_on_Kubernetes()
    {
        // The executor calls CreateSecret and PatchSecretKey by interface; a rename that looks
        // harmless here is a failed reconcile there.
        Assert.IsAssignableFrom<IKubernetesClient>(ClientOver(new FakeApiServer()));
    }

    [Fact]
    public async Task A_token_in_a_transport_failure_is_redacted_from_a_namespace_error_too()
    {
        var handler = new FakeApiServer(new HttpRequestException($"write EPROTO; Bearer {Token}"));
        var result = Assert.Single(await ClientOver(handler).ObserveNamespacesAsync(["balenthiran"]));
        Assert.DoesNotContain(Token, result.Error);
        Assert.Contains("<redacted>", result.Error);
    }

    [Fact]
    public async Task A_conflicting_patch_sends_exactly_one_write_and_does_not_retry()
    {
        var handler = new FakeApiServer(Ok(200, LiveSecret()), ApiStatus(409, "modified", "Conflict"));
        await Assert.ThrowsAsync<KubernetesApiException>(() => ClientOver(handler).PatchSecretKeyAsync("balenthiran", "balenthiran-secrets", "K", "v"));
        Assert.Equal(["GET", "PATCH"], handler.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task A_Secret_with_an_empty_resourceVersion_is_refused()
    {
        var handler = new FakeApiServer(Ok(200, LiveSecret(resourceVersion: "")));
        await Assert.ThrowsAsync<SecretWriteRefusedException>(() => ClientOver(handler).PatchSecretKeyAsync("balenthiran", "balenthiran-secrets", "K", "v"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_reported_as_a_namespace_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeApiServer(EmptyList());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientOver(handler).ObserveNamespacesAsync(["a"], cts.Token));
    }
}
