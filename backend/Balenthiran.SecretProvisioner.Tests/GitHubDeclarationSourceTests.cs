using System.Net;
using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Database;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>Port of the whole-load cases of <c>src/declarations.test.js</c>, with HTTP faked by a recording handler.</summary>
public class GitHubDeclarationSourceTests
{
    private const string Sha = DeclarationParserTests.ListingSha;
    private const string Api = "https://api.github.com/repos/jemmy8oy-northstar/oke-fleet";
    private const string Raw = $"https://raw.githubusercontent.com/jemmy8oy-northstar/oke-fleet/{Sha}/secrets";

    private static string Body(string app) => JsonSerializer.Serialize(
        new { app, @namespace = "balenthiran", secretName = $"{app}-secrets", keys = new[] { new { key = "K", type = "random" } } });

    /// <summary>A handler that answers this estate's real URLs; <paramref name="fail"/> forces a status for chosen ones.</summary>
    private static RecordingHandler Estate(string sha = Sha, Func<string, HttpStatusCode?>? fail = null) => new(request =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (fail?.Invoke(url) is { } status)
        {
            var failed = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
            failed.Headers.Add("x-ratelimit-remaining", "0");
            return failed;
        }

        if (url.Contains("/commits/", StringComparison.Ordinal))
        {
            return RecordingHandler.Text($"{sha}\n");
        }

        if (url.Contains("/contents/", StringComparison.Ordinal))
        {
            return RecordingHandler.Text(File.ReadAllText(Json.Fixture("Declarations", "contents-listing.json")));
        }

        return RecordingHandler.Text(Body(url.Split('/')[^1].Replace(".json", string.Empty, StringComparison.Ordinal)));
    });

    private static GitHubDeclarationSource SourceOver(RecordingHandler handler, DeclarationSourceLocation? location = null) =>
        new(new HttpClient(handler), new DeclarationParser(), location);

    [Fact]
    public async Task The_load_resolves_the_branch_once_then_reads_everything_at_that_commit()
    {
        var handler = Estate();
        var set = await SourceOver(handler).LoadAsync();

        Assert.Equal(("jemmy8oy-northstar", "oke-fleet", "dev", Sha), (set.Owner, set.Repo, set.Branch, set.Sha));
        Assert.Equal(3, set.Declarations.Count);

        // The exact sequence the JS makes: resolve to a bare SHA, list AT that SHA, then each file
        // at its pinned download_url. Listing at `dev` and then fetching at `dev` would be two reads
        // of a moving branch, and a push between them yields a set that never existed.
        var requests = handler.Requests;
        Assert.Equal(new RecordedRequest($"{Api}/commits/dev", "application/vnd.github.sha", "secret-provisioner"), requests[0]);
        Assert.Equal(new RecordedRequest($"{Api}/contents/secrets?ref={Sha}", "application/vnd.github+json", "secret-provisioner"), requests[1]);
        Assert.Equal(
            [$"{Raw}/around-the-world.json", $"{Raw}/balenthiran.json", $"{Raw}/macro-metrics.json"],
            requests.Skip(2).Select(r => r.Url).Order(StringComparer.Ordinal));
        Assert.All(requests.Skip(2), r => Assert.Equal("text/plain", r.Accept));
        Assert.Equal(5, requests.Count);
    }

    [Fact]
    public async Task Every_request_identifies_itself()
    {
        var handler = Estate();
        await SourceOver(handler).LoadAsync();
        Assert.All(handler.Requests, r => Assert.Equal("secret-provisioner", r.UserAgent));
    }

    [Fact]
    public async Task Only_two_requests_touch_the_API_host_and_the_rest_go_to_raw()
    {
        // The unauthenticated GitHub API allows 60 requests an hour PER IP, shared with everything
        // else this pod does. raw.githubusercontent does not spend that budget, so a reconcile costs
        // 2 requests however many apps exist.
        var handler = Estate();
        await SourceOver(handler).LoadAsync();
        Assert.Equal(2, handler.Requests.Count(r => r.Url.StartsWith("https://api.github.com/", StringComparison.Ordinal)));
        Assert.Equal(3, handler.Requests.Count(r => r.Url.StartsWith("https://raw.githubusercontent.com/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ONE_file_failing_fails_the_whole_load_because_a_partial_read_is_not_a_smaller_estate()
    {
        // The silent failure this class exists to prevent. A create-only provisioner handed two of
        // three declarations damages nothing: it creates two Secrets, reports success, and leaves
        // one app unprovisioned with nothing anywhere saying so.
        var handler = Estate(fail: url => url.EndsWith("macro-metrics.json", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : null);
        var error = await Assert.ThrowsAsync<DeclarationFetchException>(() => SourceOver(handler).LoadAsync());
        Assert.Contains("macro-metrics.json answered 500", error.Message);
    }

    [Fact]
    public async Task A_rate_limited_request_says_so_and_says_how_much_quota_is_left()
    {
        // 403-because-rate-limited fixes itself in under an hour; 403-because-something-else does
        // not, and neither does a 404 on a wrong ref. The status alone cannot tell them apart.
        var handler = Estate(fail: url => url.Contains("/commits/", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : null);
        var error = await Assert.ThrowsAsync<DeclarationFetchException>(() => SourceOver(handler).LoadAsync());
        Assert.Contains("answered 403, 0 API requests left this hour", error.Message);
    }

    [Fact]
    public async Task A_failure_without_rate_limit_headers_still_names_the_status_and_the_URL()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<DeclarationFetchException>(() => SourceOver(handler).LoadAsync());
        Assert.Equal($"GET {Api}/commits/dev answered 404", error.Message);
    }

    [Fact]
    public async Task An_unreachable_host_is_a_fetch_failure_that_keeps_its_cause()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("no route"));
        var error = await Assert.ThrowsAsync<DeclarationFetchException>(() => SourceOver(handler).LoadAsync());
        Assert.Contains("failed: no route", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Theory]
    [InlineData("<!DOCTYPE html>")]
    [InlineData("")]
    [InlineData("1fa08255183319b82ea228db097534557f6564d")]
    [InlineData("1FA08255183319B82EA228DB097534557F6564DF")]
    public async Task A_branch_that_resolves_to_something_that_is_not_a_SHA_stops_the_load(string resolved)
    {
        // A redirect, an HTML error page, or a 200 with an empty body all reach here as a
        // "successful" resolve. Concatenating that into the next URL would produce a 404 three calls
        // later, blamed on the wrong thing.
        var handler = Estate(sha: resolved);
        var error = await Assert.ThrowsAsync<DeclarationFetchException>(() => SourceOver(handler).LoadAsync());
        Assert.Contains("which is not a commit SHA", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_listing_that_is_not_JSON_is_a_fetch_failure()
    {
        var handler = new RecordingHandler(request => RecordingHandler.Text(
            request.RequestUri!.AbsoluteUri.Contains("/commits/", StringComparison.Ordinal) ? Sha : "<html>"));
        var error = await Assert.ThrowsAsync<DeclarationFetchException>(() => SourceOver(handler).LoadAsync());
        Assert.Contains("was not JSON", error.Message);
    }

    [Fact]
    public async Task HEAD_is_refused_before_a_single_request_is_made()
    {
        var handler = Estate();
        var head = new DeclarationSourceLocation { Owner = "o", Repo = "r", Branch = "HEAD", Dir = "secrets" };
        await Assert.ThrowsAsync<InvalidDeclarationSourceException>(() => SourceOver(handler, head).LoadAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_source_descriptor_decides_which_repository_branch_and_directory_are_read()
    {
        var handler = Estate();
        var elsewhere = new DeclarationSourceLocation { Owner = "o", Repo = "r", Branch = "staging", Dir = "decl" };
        await SourceOver(handler, elsewhere).LoadAsync();
        Assert.Equal("https://api.github.com/repos/o/r/commits/staging", handler.Requests[0].Url);
        Assert.Equal($"https://api.github.com/repos/o/r/contents/decl?ref={Sha}", handler.Requests[1].Url);
    }

    [Fact]
    public void The_default_source_is_dev_and_that_is_the_reason_the_provisioner_runs_before_the_release()
    {
        Assert.Equal(("jemmy8oy-northstar", "oke-fleet", "dev", "secrets"), (DeclarationSourceLocation.OkeFleet.Owner, DeclarationSourceLocation.OkeFleet.Repo, DeclarationSourceLocation.OkeFleet.Branch, DeclarationSourceLocation.OkeFleet.Dir));
    }

    [Fact]
    public async Task No_declaration_is_invented_what_comes_back_is_what_was_fetched()
    {
        var set = await SourceOver(Estate()).LoadAsync();
        Assert.Equal(["around-the-world", "balenthiran", "macro-metrics"], set.Declarations.Select(d => d.App));
    }

    [Fact]
    public async Task A_file_that_is_not_JSON_fails_the_load_and_names_the_file()
    {
        var handler = new RecordingHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/commits/", StringComparison.Ordinal))
            {
                return RecordingHandler.Text(Sha);
            }

            return RecordingHandler.Text(url.Contains("/contents/", StringComparison.Ordinal)
                ? File.ReadAllText(Json.Fixture("Declarations", "contents-listing.json"))
                : "404: Not Found");
        });
        var error = await Assert.ThrowsAsync<InvalidDeclarationException>(() => SourceOver(handler).LoadAsync());
        Assert.StartsWith("secrets/around-the-world.json: not JSON", error.Message);
    }
}
