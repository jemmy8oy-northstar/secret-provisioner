using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>Counts of each bucket and each step kind.</summary>
public sealed class PlanSummary : IPlanSummary
{
    [JsonPropertyOrder(1)]
    public required int Steps { get; init; }

    [JsonPropertyOrder(2)]
    public required int CreateSecret { get; init; }

    [JsonPropertyOrder(4)]
    public required int CreateRole { get; init; }

    [JsonPropertyOrder(5)]
    public required int CreateDatabase { get; init; }

    [JsonPropertyOrder(6)]
    public required int Blocked { get; init; }

    [JsonPropertyOrder(7)]
    public required int Unknown { get; init; }

    [JsonPropertyOrder(8)]
    public required int Drift { get; init; }

    [JsonPropertyOrder(9)]
    public required int Unmanaged { get; init; }

    [JsonPropertyOrder(10)]
    public required int Noop { get; init; }
}
