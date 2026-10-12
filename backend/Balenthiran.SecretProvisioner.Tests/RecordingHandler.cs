using System.Net;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// A hand-written <see cref="HttpMessageHandler"/>: records every request, answers from a
/// function. No mocking library — the request sequence is the thing under test.
/// </summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly object gate = new();
    private readonly List<RecordedRequest> requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    public static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            requests.Add(new RecordedRequest(
                request.RequestUri!.AbsoluteUri,
                string.Join(",", request.Headers.GetValues("Accept")),
                string.Join(" ", request.Headers.GetValues("User-Agent"))));
        }

        return Task.FromResult(respond(request));
    }
}
