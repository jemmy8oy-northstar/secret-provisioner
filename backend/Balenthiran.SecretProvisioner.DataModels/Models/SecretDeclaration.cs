using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>One <c>oke-fleet/secrets/*.json</c> file: which Secret, in which namespace, and how each key comes to exist. Never a value.</summary>
public sealed class SecretDeclaration : ISecretDeclaration
{
    [JsonPropertyOrder(1)]
    public required string App { get; init; }

    [JsonPropertyOrder(2)]
    public required string Namespace { get; init; }

    [JsonPropertyOrder(3)]
    public required string SecretName { get; init; }

    [JsonPropertyOrder(4)]
    public required List<KeyDeclaration> Keys { get; init; }

    IReadOnlyList<IKeyDeclaration> ISecretDeclaration.Keys => Keys;
}
