using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>A namespace in which a Secret probe failed, and why.</summary>
public sealed class NamespaceFailure : INamespaceFailure
{
    [JsonPropertyOrder(1)]
    public required string Namespace { get; init; }

    [JsonPropertyOrder(2)]
    public required string Error { get; init; }
}
