using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>
/// What a run did. This is the only thing that leaves the executor, and what <c>POST /reconcile</c>
/// will serialise to an HTTP response, so it holds identities and never a generated value.
/// </summary>
public sealed class ExecutionReport : IExecutionReport
{
    [JsonPropertyOrder(1)]
    public required List<ExecutedStep> Done { get; init; }

    [JsonPropertyOrder(2)]
    public FailedStep? Failed { get; init; }

    /// <summary>Steps after the failure, not attempted.</summary>
    [JsonPropertyOrder(3)]
    public required List<ExecutedStep> Skipped { get; init; }

    // The plan buckets are carried through as the plan's own entries, whatever implements them:
    // restating them in this model's words would be a second chance to get them wrong.

    /// <summary>Verbatim from the plan: the planner's refusals.</summary>
    [JsonPropertyOrder(4)]
    public required IReadOnlyList<IPlanEntry> Blocked { get; init; }

    [JsonPropertyOrder(5)]
    public required IReadOnlyList<IPlanEntry> Unknown { get; init; }

    [JsonPropertyOrder(6)]
    public required IReadOnlyList<IPlanEntry> Drift { get; init; }

    [JsonPropertyOrder(7)]
    public required IReadOnlyList<IPlanEntry> Unmanaged { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<IPlanEntry> Noop { get; init; }

    [JsonPropertyOrder(9)]
    public required ExecutionSummary Summary { get; init; }

    IReadOnlyList<IExecutedStep> IExecutionReport.Done => Done;
    IFailedStep? IExecutionReport.Failed => Failed;
    IReadOnlyList<IExecutedStep> IExecutionReport.Skipped => Skipped;
    IExecutionSummary IExecutionReport.Summary => Summary;
}
