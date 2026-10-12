namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// What each route does. The router only decides the shape of a request and the shape of a reply;
/// it does not know what a Secret is. Injecting these is what lets the rules that make an
/// unauthenticated endpoint safe be decided by a test rather than by reasoning about a cluster
/// nobody can reach from here.
/// </summary>
/// <remarks>
/// A handler returns whatever its own structured reply is and the router serialises it as the 200
/// body. A handler that throws becomes a 500 whose body never contains the exception's message.
/// </remarks>
public interface IProvisionerHandlers
{
    /// <summary>What exists — namespace, secret, key, state. Never a value.</summary>
    Task<object> StatusAsync(CancellationToken cancellationToken);

    /// <summary>What a reconcile WOULD do. Changes nothing.</summary>
    Task<object> PlanAsync(CancellationToken cancellationToken);

    /// <summary>Does it. The router guarantees at most one of these runs at a time.</summary>
    Task<object> ReconcileAsync(CancellationToken cancellationToken);
}
