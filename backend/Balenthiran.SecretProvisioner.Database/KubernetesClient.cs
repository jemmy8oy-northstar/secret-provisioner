using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Database;

/// <summary>
/// The Kubernetes edge. Port of <c>src/kube.js</c>: the only class that opens a socket to a cluster.
/// </summary>
/// <remarks>
/// NO CLIENT LIBRARY, AND THAT IS A DECISION. What is needed is four requests against three URLs,
/// and a library brings a large dependency tree into a container whose entire job is handling
/// credentials. The cost is that the request shapes below are ours to get right, which is what the
/// tests and <c>mutants/kube.json</c> are for. If this grows to need watches or informers, that
/// trade flips — take the library then, in a PR that says so.
///
/// TWO RULES ABOUT WHAT MAY BE QUOTED, AND THEY ARE OPPOSITES. A non-2xx body MAY be quoted into an
/// error: it is a <c>v1.Status</c>, whose message is the only useful thing anyone gets when a call
/// fails, and it holds no Secret values. A 2xx body may NEVER be: <c>GET /secrets/&lt;name&gt;</c>
/// answers 200 with every value in that Secret in <c>data</c>, and the patch path reads exactly one
/// such response. And the service-account token is redacted from everything unconditionally,
/// because it rides in a header and transport errors are known to echo request details.
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
    private readonly IObserver observer;

    /// <param name="http">Already pointed at the API server. See <see cref="KubernetesHttpClientFactory"/> for the real one.</param>
    /// <param name="token">The service-account token. Sent as a bearer credential and redacted from every message.</param>
    /// <param name="observer">For <see cref="IObserver.KeysOf"/>, so the key check is the observer's and not a second copy of it.</param>
    public KubernetesClient(HttpClient http, string token, IObserver observer)
    {
        this.http = http;
        this.token = token;
        this.observer = observer;
    }

    /// <summary>
    /// The Secrets in one namespace, as <c>v1.SecretList</c>. The values come back in this response
    /// and are dropped by the observer at the first point anything reads it. They are not dropped
    /// here, because doing it in two places means the one that matters can be removed while the
    /// other still looks like it is doing the job.
    /// </summary>
    public async Task<IKubeSecretList> ListSecretsAsync(string ns, CancellationToken cancellationToken = default) =>
        await ListAsync(ns, cancellationToken);

    private async Task<KubeSecretList> ListAsync(string ns, CancellationToken cancellationToken)
    {
        var text = await CallAsync($"listing Secrets in {ns}", HttpMethod.Get, $"/api/v1/namespaces/{Uri.EscapeDataString(ns)}/secrets", null, null, cancellationToken);
        var listing = ParseOrNull<KubeSecretList>(text);

        // NOT an empty listing. A response we cannot parse is a namespace we did not read, and
        // the difference between those two is the difference between a correct plan and one
        // that deletes four keys out of a live Secret.
        return listing?.Items is null
            ? throw new KubernetesApiException($"listing Secrets in {ns}: the API answered 200 with something that is not a v1.SecretList")
            : listing;
    }

    /// <summary>
    /// One attempt per namespace, in order.
    ///
    /// ⚠️ THE ENTIRE CONTRACT OF THIS METHOD IS THE <c>catch</c>. A namespace whose listing failed
    /// comes back with its error and NO listing, so the observer keeps it out of the enumerated
    /// namespaces and the planner refuses to decide anything in it. Turning the failure into an
    /// empty listing — the natural, tidy-looking thing to write — makes a transient 503
    /// indistinguishable from an empty namespace, and the planner then plans a create for a Secret
    /// that already exists. Nor may a failure be dropped from the result: nothing destructive
    /// would follow, but the run would report undecidable keys with no stated reason.
    ///
    /// Sequential, not parallel: this runs against a handful of namespaces on a human-triggered
    /// endpoint, and a serial loop cannot produce a burst that the API server's priority-and-fairness
    /// queue sheds — which would arrive here as exactly the spurious per-namespace failure above.
    /// </summary>
    public async Task<IReadOnlyList<INamespaceListing>> ObserveNamespacesAsync(IEnumerable<string> namespaces, CancellationToken cancellationToken = default)
    {
        var results = new List<INamespaceListing>();
        foreach (var ns in namespaces)
        {
            try
            {
                results.Add(new NamespaceListing { Namespace = ns, Listing = await ListAsync(ns, cancellationToken) });
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                results.Add(new NamespaceListing { Namespace = ns, Error = Redact(e.Message) });
            }
        }

        return results;
    }

    /// <summary>
    /// Create a Secret.
    ///
    /// **POST, and it must stay POST.** A PUT to the item path is an unconditional replace, and an
    /// apply-patch is a replace with better manners: either silently discards every key in an
    /// existing Secret that is not in <paramref name="data"/>. The plan was made against an
    /// observation taken moments earlier, so "does it exist" cannot be answered here without a
    /// race. The 409 the API server returns is not an inconvenience to work around — it is the
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
    /// Add one key to an existing Secret.
    ///
    /// CREATE-ONLY WITHOUT A RACE, WHICH TAKES BOTH HALVES BELOW. There is no server-side "add this
    /// map key only if absent" — a merge patch overwrites. So the absence check happens here, and a
    /// bare check-then-act would be the race create-only exists to rule out. The fix is that the
    /// check and the write are tied together by <c>resourceVersion</c>: a merge patch carrying
    /// <c>metadata.resourceVersion</c> is a conditional update, and the API server answers 409 if
    /// the Secret changed at all since the read. So either nothing touched it between the two calls
    /// and the absence we observed still holds, or the write is refused.
    ///
    /// ⚠️ The read answers 200 with every existing value in <c>data</c>. The observer's
    /// <see cref="IObserver.KeysOf"/> takes the key names and the rest is discarded with the
    /// response. Nothing about that response is put into an error message.
    /// </summary>
    public async Task PatchSecretKeyAsync(string ns, string name, string key, string value, CancellationToken cancellationToken = default)
    {
        var at = $"{ns}/{name}#{key}";
        var path = $"/api/v1/namespaces/{Uri.EscapeDataString(ns)}/secrets/{Uri.EscapeDataString(name)}";

        var text = await CallAsync($"reading Secret {ns}/{name} before adding {key}", HttpMethod.Get, path, null, null, cancellationToken);
        var secret = ParseOrNull<KubeSecret>(text);

        var resourceVersion = secret?.Metadata?.ResourceVersion;
        if (string.IsNullOrEmpty(resourceVersion))
        {
            // Without it the patch below stops being conditional and quietly becomes the
            // unguarded overwrite this method is built to avoid. Refuse instead.
            throw new SecretWriteRefusedException($"refusing to patch {at}: the API returned the Secret with no metadata.resourceVersion, so the write could not be made conditional");
        }

        if (observer.KeysOf(secret).Contains(key))
        {
            throw new SecretWriteRefusedException($"refusing to patch {at}: the key is already present, and this provisioner never overwrites a value it did not create");
        }

        // `stringData` only. A merge patch sends exactly what it names, so the other keys'
        // values are neither re-sent nor even referenced — they were read once, above, and
        // discarded.
        await CallAsync(
            $"adding {key} to Secret {ns}/{name}",
            HttpMethod.Patch,
            path,
            "application/merge-patch+json",
            new { metadata = new { resourceVersion }, stringData = new Dictionary<string, string> { [key] = value } },
            cancellationToken);
    }

    /// <summary>Parse a body that should be JSON, without letting a proxy's HTML take the process down.</summary>
    private static T? ParseOrNull<T>(string text)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
