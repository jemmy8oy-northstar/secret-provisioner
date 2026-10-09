using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// The three things the service does, each assembled from the same four reads: the declarations
/// in oke-fleet, the Secrets in every namespace they name, and the Postgres catalogue — then the
/// planner, and for a reconcile the executor. This is where the slices meet; nothing here decides
/// anything the planner or the executor already decides.
/// </summary>
/// <remarks>
/// EVERY CALL READS AFRESH. A reconcile does not reuse the plan a previous <c>/plan</c> showed,
/// because the estate can change between the two and create-only makes a stale plan's mistake
/// permanent. What <c>/plan</c> shows is what a reconcile at that moment would do, not a promise
/// about the next one.
///
/// A POSTGRES THAT CANNOT BE READ FAILS THE CALL rather than being observed as "not inspected".
/// The planner can plan without Postgres, but then every database key is <c>unknown</c>, and a
/// report full of unknowns reads like a design gap rather than an outage. The 500 says which.
/// </remarks>
public interface IReconciler
{
    /// <summary>What exists: the observation, keyed by namespace and Secret. Key names only, never a value.</summary>
    Task<IProvisionerView> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>What a reconcile would do now. Changes nothing.</summary>
    Task<IProvisionerView> PlanAsync(CancellationToken cancellationToken = default);

    /// <summary>Plan against a fresh observation, then execute it. The report carries no generated value.</summary>
    Task<IProvisionerView> ReconcileAsync(CancellationToken cancellationToken = default);
}
