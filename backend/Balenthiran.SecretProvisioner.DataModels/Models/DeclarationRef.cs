using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>Where the declarations were read from, without the declarations: the audit trail a response carries.</summary>
public sealed class DeclarationRef : IDeclarationRef
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
}
