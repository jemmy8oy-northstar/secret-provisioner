using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>Where the Postgres server is, as the apps' connection strings will name it.</summary>
public sealed class PostgresEndpoint : IPostgresEndpoint
{
    /// <summary>Required, with no default: a guessed host composes a connection string that is syntactically perfect and resolves to nothing.</summary>
    [JsonPropertyOrder(1)]
    public required string Host { get; init; }

    /// <summary>Absent means 5432.</summary>
    [JsonPropertyOrder(2)]
    public int? Port { get; init; }
}
