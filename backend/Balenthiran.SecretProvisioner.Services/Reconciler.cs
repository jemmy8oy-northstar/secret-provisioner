using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services;

/// <summary>
/// See <see cref="IReconciler"/>. Composition only: the reads go to the clients, the decisions to
/// the planner and the executor, and this class does nothing but put them in order.
/// </summary>
public sealed class Reconciler : IReconciler
{
    private readonly IDeclarationSource _source;
    private readonly IKubernetesClient _kubernetes;
    private readonly IPostgresClient _postgres;
    private readonly IObserver _observer;
    private readonly IPlanner _planner;
    private readonly IExecutor _executor;
    private readonly IPostgresEndpoint _endpoint;

    /// <param name="endpoint">Where the APPS reach Postgres — what goes into a composed connection string, not necessarily what this pod dials.</param>
    public Reconciler(IDeclarationSource source, IKubernetesClient kubernetes, IPostgresClient postgres, IObserver observer, IPlanner planner, IExecutor executor, IPostgresEndpoint endpoint)
    {
        _source = source;
        _kubernetes = kubernetes;
        _postgres = postgres;
        _observer = observer;
        _planner = planner;
        _executor = executor;
        _endpoint = endpoint;
    }

    public async Task<IProvisionerView> StatusAsync(CancellationToken cancellationToken = default)
    {
        var (set, observation) = await ObserveAsync(cancellationToken);
        return new ProvisionerView { Source = RefOf(set), Observation = observation };
    }

    public async Task<IProvisionerView> PlanAsync(CancellationToken cancellationToken = default)
    {
        var (set, observation) = await ObserveAsync(cancellationToken);
        return new ProvisionerView { Source = RefOf(set), Observation = observation, Plan = _planner.Plan(set.Declarations, observation) };
    }

    public async Task<IProvisionerView> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var (set, observation) = await ObserveAsync(cancellationToken);
        var plan = _planner.Plan(set.Declarations, observation);

        // The SAME declarations the plan was made from. The executor resolves each step's
        // parameters from them, and a second read could land on a newer commit than the plan's.
        var report = await _executor.ExecuteAsync(plan, set.Declarations, _endpoint, cancellationToken);
        return new ProvisionerView { Source = RefOf(set), Observation = observation, Plan = plan, Report = report };
    }

    private async Task<(IDeclarationSet Set, IObservation Observation)> ObserveAsync(CancellationToken cancellationToken)
    {
        var set = await _source.LoadAsync(cancellationToken);

        // Only the namespaces something is declared in. Listing every namespace would need a
        // cluster-wide RBAC grant for no answer the planner uses.
        var namespaces = set.Declarations.Select(d => d.Namespace).Distinct(StringComparer.Ordinal).ToList();
        var listings = await _kubernetes.ObserveNamespacesAsync(namespaces, cancellationToken);
        var catalogue = await _postgres.ReadCatalogueAsync(cancellationToken);

        return (set, _observer.Observe(listings, _observer.IndexPostgres(catalogue.RoleNames, catalogue.DatabaseNames)));
    }

    private static DeclarationRef RefOf(IDeclarationSet set) =>
        new() { Owner = set.Owner, Repo = set.Repo, Branch = set.Branch, Sha = set.Sha };
}
