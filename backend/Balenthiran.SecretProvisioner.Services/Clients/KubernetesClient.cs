using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services.Clients;

/// <summary>
/// The Kubernetes edge. Port of <c>src/kube.js</c>: the only class that opens a socket to a cluster.
/// </summary>
/// <remarks>
/// NO CLIENT LIBRARY, AND THAT IS A DECISION. What is needed is one request against one URL,
/// and a library brings a large dependency tree into a container whose entire job is handling
/// credentials. The cost is that the request shapes below are ours to get right, which is what the
/// tests and <c>mutants/kube.json</c> are for. If this grows to need watches or informers, that
/// trade flips — take the library then, in a PR that says so.
///
/// TWO RULES ABOUT WHAT MAY BE QUOTED, AND THEY ARE OPPOSITES. A non-2xx body MAY be quoted into an
/// error: it is a <c>v1.Status</c>, whose message is the only useful thing anyone gets when a call
/// fails, and it holds no Secret values. A 2xx body is never quoted either, and is never parsed:
/// the only 2xx this client receives is the echo of what it sent. And the service-account token is
/// redacted from everything unconditionally, because it rides in a header and transport errors are
/// known to echo request details.
///
/// IT CANNOT READ A SECRET, BY CONSTRUCTION (James, secret-provisioner#9). The Role grants
/// <c>create</c> on Secrets and nothing else, so there is no GET, no list and no patch here to get
/// wrong: the cluster refuses them before this code could misuse the answer.
/// </remarks>
public sealed class KubernetesClient : IKubernetesClient
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Raw UTF-8, as the JS wrote it: with the default escaping a multi-byte name would arrive
        // as ASCII `é` and the byte-length rule below would be asserting nothing.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient http;
    private readonly string token;

    /// <param name="http">Already pointed at the API server. See <see cref="KubernetesHttpClientFactory"/> for the real one.</param>
    /// <param name="token">The service-account token. Sent as a bearer credential and redacted from every message.</param>
    public KubernetesClient(HttpClient http, string token)
    {
        this.http = http;
        this.token = token;
    }

    /// <summary>
    /// Does this Secret exist? Asked as a CREATE with <c>dryRun=All</c>, because <c>create</c> is the
    /// only verb this pod holds. The API server runs the request through validation and admission and
    /// stops short of storage: an existing name answers 409 <c>AlreadyExists</c>, a free one answers
    /// 201 with the echo of the empty Secret sent, and nothing is written either way.
    ///
    /// ⚠️ ONLY <c>AlreadyExists</c> MEANS "EXISTS". Any other refusal — 403 for a namespace the Role
    /// does not cover, a 409 for some other conflict, a 503 — is an error, not an answer. Reading it as
    /// "absent" would plan a real create for a Secret that may well exist, and reading it as "exists"
    /// would hide a Secret that was never made.
    /// </summary>
    private async Task<bool> ExistsAsync(string ns, string name, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync(
                $"probing Secret {ns}/{name}",
                HttpMethod.Post,
                $"/api/v1/namespaces/{Uri.EscapeDataString(ns)}/secrets?dryRun=All",
                "application/json",
                new { apiVersion = "v1", kind = "Secret", type = "Opaque", metadata = new { name, @namespace = ns } },
                cancellationToken);
            return false;
        }
        catch (KubernetesApiException e) when (e.Status == 409 && e.Reason == "AlreadyExists")
        {
            return true;
        }
    }

    /// <summary>
    /// One probe per Secret, in order.
    ///
    /// ⚠️ THE ENTIRE CONTRACT OF THIS METHOD IS THE <c>catch</c>. A probe that failed comes back with
    /// its error and NO answer, so the observer keeps its namespace out of the enumerated namespaces
    /// and the planner refuses to decide anything in it. Turning the failure into "absent" — the
    /// natural, tidy-looking thing to write — makes a transient 503 indistinguishable from a free
    /// name, and the planner then plans a create for a Secret that already exists.
    ///
    /// Sequential, not parallel: this runs against a handful of Secrets on a human-triggered endpoint,
    /// and a serial loop cannot produce a burst that the API server's priority-and-fairness queue
    /// sheds — which would arrive here as exactly the spurious failure above.
    /// </summary>
    public async Task<IReadOnlyList<ISecretProbe>> ProbeSecretsAsync(IEnumerable<(string Namespace, string Name)> secrets, CancellationToken cancellationToken = default)
    {
        var results = new List<ISecretProbe>();
        foreach (var (ns, name) in secrets)
        {
            try
            {
                results.Add(new SecretProbe { Namespace = ns, Name = name, Exists = await ExistsAsync(ns, name, cancellationToken) });
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                results.Add(new SecretProbe { Namespace = ns, Name = name, Error = Redact(e.Message) });
            }
        }

        return results;
    }

    /// <summary>
    /// Create a Secret.
    ///
    /// **POST, and it must stay POST.** A PUT to the item path is an unconditional replace, and an
    /// apply-patch is a replace with better manners: either silently discards every key in an
    /// existing Secret that is not in <paramref name="data"/>. The plan was made against a probe
    /// taken moments earlier, so "does it exist" cannot be answered here without a race. The 409 the API server returns is not an inconvenience to work around — it is the
    /// entire create-only guarantee, enforced by the only party that can enforce it.
    ///
    /// Values go in <c>stringData</c>, so nothing in this repo ever base64-encodes a secret. The API
    /// server does the encoding; there is no encode step to get wrong in a way that produces a
    /// Secret which mounts as plausible garbage.
    /// </summary>
    public async Task CreateSecretAsync(string ns, string name, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default) =>
        await CallAsync(
            $"creating Secret {ns}/{name}",
            HttpMethod.Post,
            $"/api/v1/namespaces/{Uri.EscapeDataString(ns)}/secrets",
            "application/json",
            new
            {
                apiVersion = "v1",
                kind = "Secret",
                type = "Opaque",
                metadata = new { name, @namespace = ns },
                stringData = data,
            },
            cancellationToken);

    /// <summary>
    /// The message and reason of a non-2xx body. The body is a <c>v1.Status</c> — see the remarks.
    /// Anything else (a proxy's HTML) is quoted for its first 500 characters.
    /// </summary>
    private static (string Message, string? Reason) ReadStatus(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return (message ?? text[..Math.Min(500, text.Length)], reason);
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the raw text.
        }

        return (text[..Math.Min(500, text.Length)], null);
    }

    private static string Describe(Exception e)
    {
        var parts = new List<string>();
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            parts.Add(inner.Message);
        }

        return string.Join(": ", parts);
    }

    private string Redact(string text) =>
        token.Length == 0 ? text : text.Replace(token, "<redacted>", StringComparison.Ordinal);

    /// <summary>One request. Returns the body of a 2xx; throws for anything else.</summary>
    private async Task<string> CallAsync(string what, HttpMethod method, string path, string? contentType, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            // UTF-8 bytes, so the Content-Length is the byte length: a multi-byte name makes the
            // two differ, and a short length truncates the body into invalid JSON that the API
            // rejects with a parse error nobody would connect to this line.
            request.Content = new StringContent(JsonSerializer.Serialize(body, Options), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType!);
        }

        int status;
        string text;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // A transport-level failure — DNS, TLS, timeout. Some of these have been known to
            // carry the request details, and the details carry the Authorization header.
            throw new KubernetesApiException(Redact($"{what}: {Describe(e)}"));
        }

        if (status is < 200 or >= 300)
        {
            var (message, reason) = ReadStatus(text);
            throw new KubernetesApiException(Redact($"{what}: the Kubernetes API answered {status} — {message}"), status, reason);
        }

        return text;
    }
}
