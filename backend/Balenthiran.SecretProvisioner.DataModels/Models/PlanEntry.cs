using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>A declared key the plan does not act on, and the reason. <see cref="Action"/> is set only on blocked entries and names the human step.</summary>
public sealed class PlanEntry : IPlanEntry
{
    [JsonPropertyOrder(1)]
    public required string App { get; init; }

    [JsonPropertyOrder(2)]
    public required string Namespace { get; init; }

    [JsonPropertyOrder(3)]
    public required string SecretName { get; init; }

    [JsonPropertyOrder(4)]
    public required string Key { get; init; }

    /// <summary>Absent on unmanaged keys: no declaration says what they are.</summary>
    [JsonPropertyOrder(5)]
    public string? Type { get; init; }

    [JsonPropertyOrder(6)]
    public string? Action { get; init; }

    [JsonPropertyOrder(7)]
    public required string Reason { get; init; }
}
