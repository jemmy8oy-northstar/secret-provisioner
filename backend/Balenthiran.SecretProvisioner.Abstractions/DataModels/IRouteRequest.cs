namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>
/// The parts of an HTTP request the router decides on. Headers arrive as the raw strings so that
/// the router, not the host, owns what counts as a body.
/// </summary>
public interface IRouteRequest
{
    string Method { get; }

    /// <summary>The raw request target: a path, possibly with a query and a fragment.</summary>
    string Url { get; }

    /// <summary>The raw <c>Content-Length</c> header, or null when absent.</summary>
    string? ContentLength { get; }

    /// <summary>The raw <c>Transfer-Encoding</c> header, or null when absent.</summary>
    string? TransferEncoding { get; }
}
