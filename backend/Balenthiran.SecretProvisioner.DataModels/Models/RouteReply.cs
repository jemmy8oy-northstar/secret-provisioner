using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>A reply, as data. Serialising it is the host's job and happens in one place.</summary>
public sealed class RouteReply : IRouteReply
{
    [JsonPropertyOrder(1)]
    public required int Status { get; init; }

    /// <summary>Serialised by the host. An error body is assembled from a fixed string, never from an exception.</summary>
    [JsonPropertyOrder(2)]
    public required object Body { get; init; }

    /// <summary>The <c>Allow</c> header, set on a 405 only.</summary>
    [JsonPropertyOrder(3)]
    public string? Allow { get; init; }

    /// <summary>
    /// The handler's exception on a 500, handed to the caller to log where redaction is that
    /// layer's job. Never serialised: it can carry the very statement that holds a password.
    /// </summary>
    [JsonIgnore]
    public Exception? Thrown { get; init; }
}
