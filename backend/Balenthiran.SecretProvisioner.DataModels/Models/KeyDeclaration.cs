using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>One key of a declared Secret. <see cref="Type"/> decides how its value comes to exist: <c>postgres-db</c>, <c>random</c> or <c>external</c>.</summary>
public sealed class KeyDeclaration : IKeyDeclaration
{
    [JsonPropertyOrder(1)]
    public required string Key { get; init; }

    [JsonPropertyOrder(2)]
    public required string Type { get; init; }

    /// <summary><c>postgres-db</c> only.</summary>
    [JsonPropertyOrder(3)]
    public string? Database { get; init; }

    /// <summary><c>postgres-db</c> only.</summary>
    [JsonPropertyOrder(4)]
    public string? Role { get; init; }

    /// <summary><c>random</c> only; absent means 32.</summary>
    [JsonPropertyOrder(5)]
    public int? Bytes { get; init; }
}
