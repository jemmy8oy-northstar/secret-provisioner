namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>
/// What a run did. This is the only thing that leaves the executor, and what <c>POST /reconcile</c>
/// will serialise to an HTTP response, so it holds identities and never a generated value.
/// </summary>
public interface IExecutionReport
{
    IReadOnlyList<IExecutedStep> Done { get; }

    IFailedStep? Failed { get; }

    /// <summary>Steps after the failure, not attempted.</summary>
    IReadOnlyList<IExecutedStep> Skipped { get; }

    /// <summary>Verbatim from the plan: the planner's refusals, not restated in the executor's words.</summary>
    IReadOnlyList<IPlanEntry> Blocked { get; }

    IReadOnlyList<IPlanEntry> Unknown { get; }

    IReadOnlyList<IPlanEntry> Drift { get; }

    IReadOnlyList<IPlanEntry> Unmanaged { get; }

    IReadOnlyList<IPlanEntry> Noop { get; }

    IExecutionSummary Summary { get; }
}
