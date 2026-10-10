using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>Every role and database name the server holds, unfiltered — the observer decides which are ours.</summary>
public sealed class PostgresCatalogue : IPostgresCatalogue
{
    [JsonPropertyOrder(1)]
    public required List<string?> RoleNames { get; init; }

    [JsonPropertyOrder(2)]
    public required List<string?> DatabaseNames { get; init; }

    IReadOnlyList<string?> IPostgresCatalogue.RoleNames => RoleNames;
    IReadOnlyList<string?> IPostgresCatalogue.DatabaseNames => DatabaseNames;
}
