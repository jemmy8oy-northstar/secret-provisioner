using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>One action. Which fields are set depends on <see cref="Kind"/>: <c>create-role</c>, <c>create-database</c> or <c>create-secret</c>.</summary>
public sealed class PlanStep : IPlanStep
{
    [JsonPropertyOrder(1)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(2)]
    public string? App { get; init; }

    [JsonPropertyOrder(3)]
    public required string Namespace { get; init; }

    [JsonPropertyOrder(4)]
    public required string SecretName { get; init; }

    [JsonPropertyOrder(5)]
    public string? Key { get; init; }

    [JsonPropertyOrder(6)]
    public string? Type { get; init; }

    [JsonPropertyOrder(7)]
    public string? Database { get; init; }

    [JsonPropertyOrder(8)]
    public string? Role { get; init; }

    [JsonPropertyOrder(9)]
    public string? Source { get; init; }

    /// <summary><c>create-secret</c> only: every key the new Secret is created with.</summary>
    [JsonPropertyOrder(10)]
    public List<StepKey>? Keys { get; init; }

    [JsonPropertyOrder(12)]
    public required string Detail { get; init; }

    IReadOnlyList<IStepKey>? IPlanStep.Keys => Keys;
}
