using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>Roles and databases that belong to the estate — system ones filtered out.</summary>
public sealed class PostgresObservation : IPostgresObservation
{
    [JsonPropertyOrder(1)]
    public List<string>? Roles { get; init; }

    [JsonPropertyOrder(2)]
    public List<string>? Databases { get; init; }

    IReadOnlyList<string>? IPostgresObservation.Roles => Roles;
    IReadOnlyList<string>? IPostgresObservation.Databases => Databases;
}
