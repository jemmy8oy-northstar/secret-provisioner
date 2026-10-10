using System.Text.Json.Serialization;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>The 404 body: says what does exist.</summary>
public sealed class NoSuchRouteBody
{
    [JsonPropertyOrder(1)]
    public string Error { get; } = "no such route";

    [JsonPropertyOrder(2)]
    public required IReadOnlyList<string> Routes { get; init; }
}
