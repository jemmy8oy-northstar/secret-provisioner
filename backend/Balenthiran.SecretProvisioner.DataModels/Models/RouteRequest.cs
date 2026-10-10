using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>
/// The parts of an HTTP request the router decides on. Headers arrive as the raw strings so that
/// the router, not the host, owns what counts as a body.
/// </summary>
public sealed class RouteRequest : IRouteRequest
{
    [JsonPropertyOrder(1)]
    public required string Method { get; init; }

    /// <summary>The raw request target: a path, possibly with a query and a fragment.</summary>
    [JsonPropertyOrder(2)]
    public required string Url { get; init; }

    /// <summary>The raw <c>Content-Length</c> header, or null when absent.</summary>
    [JsonPropertyOrder(3)]
    public string? ContentLength { get; init; }

    /// <summary>The raw <c>Transfer-Encoding</c> header, or null when absent.</summary>
    [JsonPropertyOrder(4)]
    public string? TransferEncoding { get; init; }
}
