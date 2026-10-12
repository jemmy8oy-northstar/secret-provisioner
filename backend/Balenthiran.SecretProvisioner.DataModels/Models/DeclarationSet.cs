using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>The declarations as read at one commit, and which commit — the audit trail for everything a reconcile does.</summary>
public sealed class DeclarationSet : IDeclarationSet
{
    [JsonPropertyOrder(1)]
    public required string Owner { get; init; }

    [JsonPropertyOrder(2)]
    public required string Repo { get; init; }

    [JsonPropertyOrder(3)]
    public required string Branch { get; init; }

    /// <summary>The 40-hex commit every file was read at.</summary>
    [JsonPropertyOrder(4)]
    public required string Sha { get; init; }

    [JsonPropertyOrder(5)]
    public required List<SecretDeclaration> Declarations { get; init; }

    IReadOnlyList<ISecretDeclaration> IDeclarationSet.Declarations => Declarations;
}
