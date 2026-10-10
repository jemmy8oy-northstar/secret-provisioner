using System.Text;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// A hand-written fake API server: records every request and replays a scripted reply, so what is
/// asserted is the exact request the cluster would receive — method, path, content type, and the
/// body byte for byte.
/// </summary>
/// <remarks>
/// Replies are consumed in order and may be a <see cref="Reply"/>, an <see cref="Exception"/> to
/// throw, or a <c>Func&lt;Sent, Reply&gt;</c> to answer from the request. Running out is a failure
/// rather than a null read, because "the client made a call the test did not expect" is a finding.
/// </remarks>
internal sealed class FakeApiServer : HttpMessageHandler
{
    private readonly Queue<object> replies;

    public FakeApiServer(params object[] replies) => this.replies = new Queue<object>(replies);

    public List<Sent> Requests { get; } = [];

    public bool Drained => replies.Count == 0;

    public static Reply Ok(int status, object body) => new(status, System.Text.Json.JsonSerializer.Serialize(body));

    public static Reply ApiStatus(int status, string message, string reason) =>
        Ok(status, new { kind = "Status", apiVersion = "v1", status = "Failure", message, reason, code = status });

    public HttpClient Client() => new(this) { BaseAddress = new Uri("https://10.96.0.1") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A real handler honours the token; a fake that did not would make cancellation untestable.
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var sent = new Sent(
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString(),
            string.Join(",", request.Headers.Accept.Select(a => a.ToString())),
            request.Content?.Headers.ContentType?.ToString(),
            request.Content?.Headers.ContentLength,
            bytes is null ? null : Encoding.UTF8.GetString(bytes),
            bytes?.Length);
        Requests.Add(sent);

        Assert.True(replies.Count > 0, $"unexpected extra request: {sent.Method} {sent.Uri}");
        var next = replies.Dequeue();
        if (next is Func<Sent, Reply> answer)
        {
            next = answer(sent);
        }

        if (next is Exception e)
        {
            throw e;
        }

        var reply = (Reply)next;
        return new HttpResponseMessage((System.Net.HttpStatusCode)reply.Status) { Content = new StringContent(reply.Text) };
    }

    internal sealed record Reply(int Status, string Text);

    internal sealed record Sent(string Method, string Uri, string? Authorization, string Accept, string? ContentType, long? ContentLength, string? Body, int? ByteCount);
}
