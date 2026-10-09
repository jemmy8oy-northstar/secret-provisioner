using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>Counts of each report bucket.</summary>
public sealed class ExecutionSummary : IExecutionSummary
{
    [JsonPropertyOrder(1)]
    public required int Done { get; init; }

    /// <summary>0 or 1: the first failure stops the run.</summary>
    [JsonPropertyOrder(2)]
    public required int Failed { get; init; }

    [JsonPropertyOrder(3)]
    public required int Skipped { get; init; }

    /// <summary>How many values this run generated. A count, never a value; it lets a test see that each one was registered for redaction.</summary>
    [JsonPropertyOrder(4)]
    public required int Generated { get; init; }

    [JsonPropertyOrder(5)]
    public required int Blocked { get; init; }

    [JsonPropertyOrder(6)]
    public required int Unknown { get; init; }

    [JsonPropertyOrder(7)]
    public required int Drift { get; init; }

    [JsonPropertyOrder(8)]
    public required int Unmanaged { get; init; }

    [JsonPropertyOrder(9)]
    public required int Noop { get; init; }
}
