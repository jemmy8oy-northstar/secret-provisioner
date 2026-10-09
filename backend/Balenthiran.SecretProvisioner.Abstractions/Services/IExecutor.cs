using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// Do what the planner decided. The plan says WHAT; the declarations say with what — a step names
/// a key but carries no byte count, role or database, so those are resolved from the declaration
/// the step names. Anything the two disagree about stops the run.
/// </summary>
/// <remarks>
/// THE FIRST FAILURE STOPS THE RUN and every later step is reported as skipped. No generated
/// value is ever in the report: errors are redacted before they are recorded, and the finished
/// report is checked for every value the run generated.
/// </remarks>
public interface IExecutor
{
    /// <summary>
    /// Execute <paramref name="plan"/>. A step that fails is recorded in the report, not thrown.
    /// Throws <see cref="Exceptions.InvalidPostgresEndpointException"/> before anything is
    /// created if <paramref name="postgres"/> has no host or a port outside 1 to 65535.
    /// </summary>
    Task<IExecutionReport> ExecuteAsync(IPlan plan, IReadOnlyList<ISecretDeclaration> declarations, IPostgresEndpoint postgres, CancellationToken cancellationToken = default);
}
