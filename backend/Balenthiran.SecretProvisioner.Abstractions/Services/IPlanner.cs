using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// Given the declarations and what was observed, decide what a provisioner WOULD do. Pure:
/// same input, same plan, no I/O. Moved here from <c>oke-fleet:scripts/plan-secrets.mjs</c>
/// (James's ruling on secret-provisioner#6: the planner lives with its only consumer, the
/// declarations stay in oke-fleet).
/// </summary>
/// <remarks>
/// CREATE-ONLY, NEVER DELETE, NEVER OVERWRITE (James, claude-code-bot#97). A key already
/// present is left alone even when it looks wrong; an existing Postgres role is never given a
/// new password. When the planner cannot proceed within those rules it says so and names the
/// human step — it never widens its own remit.
/// </remarks>
public interface IPlanner
{
    /// <summary>
    /// Plan. Throws <see cref="Exceptions.InvalidObservationException"/> for an observation that
    /// does not say what it looked at.
    /// </summary>
    IPlan Plan(IReadOnlyList<ISecretDeclaration> declarations, IObservation observation);
}
