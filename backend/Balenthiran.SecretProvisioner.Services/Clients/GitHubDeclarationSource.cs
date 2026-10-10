using System.Net;
using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services.Clients;

/// <summary>
/// Port of the HTTP half of <c>src/declarations.js</c>: reads <c>oke-fleet/secrets/*.json</c> over
/// HTTPS, at one commit, and refuses to hand back a partial answer. Everything it decides about
/// what came back is delegated to <see cref="IDeclarationParser"/>.
/// </summary>
/// <remarks>
/// Declarations stay in <c>oke-fleet</c> and are read at reconcile time rather than baked into the
/// image, so adding an app is a data change in a config repo and not a container rebuild. No
/// credential is involved, because <c>oke-fleet</c> is public.
///
/// ONE COMMIT, NOT ONE BRANCH. The branch is resolved to a commit SHA ONCE, and the listing and
/// every file are read at that SHA. Reading <c>secrets/</c> at <c>dev</c> and then fetching each file
/// at <c>dev</c> is two reads of a moving branch, and a push landing between them yields a set that
/// never existed as a commit. It also gives the report something exact to name — "provisioned from
/// oke-fleet@sha" — the only audit trail that survives the fact that no value may ever be logged.
///
/// Two calls land on <c>api.github.com</c> (resolve the branch, list the directory) and the rest on
/// <c>raw.githubusercontent.com</c>, which does not spend the unauthenticated 60-requests-per-hour
/// API budget. That is why one contents call per file is not used: the cost of a reconcile would
/// grow with the number of apps, on a quota that is per IP and shared with everything else this pod does.
/// </remarks>
public sealed class GitHubDeclarationSource : IDeclarationSource
{
    private const string UserAgent = "secret-provisioner";

    private readonly HttpClient http;
    private readonly IDeclarationParser parser;
    private readonly IDeclarationSourceLocation source;

    /// <param name="http">The client to read with; its base address is never used, every URL is absolute.</param>
    /// <param name="parser">Decides what the responses mean.</param>
    /// <param name="source">Where to read. Absent means <c>oke-fleet</c>'s <c>secrets/</c> on <c>dev</c>.</param>
    public GitHubDeclarationSource(HttpClient http, IDeclarationParser parser, IDeclarationSourceLocation? source = null)
    {
        this.http = http;
        this.parser = parser;
        this.source = source ?? DeclarationSourceLocation.OkeFleet;
    }

    public async Task<IDeclarationSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        var (owner, repo, dir) = (source.Owner, source.Repo, source.Dir);
        var branch = parser.AssertBranch(source.Branch);
        var api = $"https://api.github.com/repos/{owner}/{repo}";

        // `Accept: application/vnd.github.sha` makes this return the bare commit SHA as the whole
        // body — no JSON, no commit object, and one request.
        var sha = (await GetTextAsync($"{api}/commits/{branch}", "application/vnd.github.sha", cancellationToken)).Trim();
        if (sha.Length != 40 || !sha.All(char.IsAsciiHexDigitLower))
        {
            throw new DeclarationFetchException($"resolving {branch} gave {Quote(sha[..Math.Min(80, sha.Length)])}, which is not a commit SHA");
        }

        var listingText = await GetTextAsync($"{api}/contents/{dir}?ref={sha}", "application/vnd.github+json", cancellationToken);
        IReadOnlyList<IDeclarationBlob> blobs;
        try
        {
            using var listing = JsonDocument.Parse(listingText);
            blobs = parser.DeclarationBlobs(listing.RootElement, dir, sha);
        }
        catch (JsonException error)
        {
            throw new DeclarationFetchException($"the contents listing for {dir} at {sha} was not JSON ({error.Message})", error);
        }

        // All of them or none. `Task.WhenAll` faults if any task does, which is the behaviour wanted
        // — but the reason it is wanted is worth stating, because settling every task and keeping
        // the fulfilled ones is the natural-looking alternative and it is exactly the partial read
        // this class exists to refuse.
        var bodies = await Task.WhenAll(blobs.Select(blob => GetTextAsync(blob.Url, "text/plain", cancellationToken)));
        var files = blobs.Select((blob, i) => (Name: blob.Path, Body: bodies[i]));

        return parser.Assemble(owner, repo, branch, sha, parser.ParseDeclarations(files));
    }

    /// <summary>A GET whose non-2xx answers throw with the status and the URL.</summary>
    private async Task<string> GetTextAsync(string url, string accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new DeclarationFetchException($"GET {url} failed: {error.Message}", error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 403 with the rate-limit headers, 404 for a wrong ref and 500 from GitHub are three
                // different problems and one of them fixes itself. The status and the remaining
                // quota are what distinguish them, so both go in the message.
                var quota = response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
                    ? $", {values.First()} API requests left this hour"
                    : string.Empty;
                throw new DeclarationFetchException($"GET {url} answered {(int)response.StatusCode}{quota}");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    private static string Quote(string text) => JsonSerializer.Serialize(text, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
