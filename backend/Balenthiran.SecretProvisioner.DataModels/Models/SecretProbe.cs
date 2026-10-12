using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>One declared Secret, asked about by a dry-run create. Exactly one of <see cref="Exists"/> and <see cref="Error"/> is set.</summary>
public sealed class SecretProbe : ISecretProbe
{
    [JsonPropertyOrder(1)]
    public required string Namespace { get; init; }

    [JsonPropertyOrder(2)]
    public required string Name { get; init; }

    [JsonPropertyOrder(3)]
    public bool? Exists { get; init; }

    [JsonPropertyOrder(4)]
    public string? Error { get; init; }
}
