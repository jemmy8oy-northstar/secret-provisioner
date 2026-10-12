using Balenthiran.SecretProvisioner.Abstractions.Services;

namespace Balenthiran.SecretProvisioner.WebApi;

/// <summary>
/// What the router calls. Each call gets its own scope and so its own <see cref="IReconciler"/>,
/// which reads the rotating service-account token afresh (see <see cref="ServiceRegistration"/>).
/// </summary>
public sealed class ScopedHandlers : IProvisionerHandlers
{
    private readonly IServiceScopeFactory _scopes;

    public ScopedHandlers(IServiceScopeFactory scopes) => _scopes = scopes;

    public Task<object> StatusAsync(CancellationToken cancellationToken) => Run((r, ct) => r.StatusAsync(ct), cancellationToken);

    public Task<object> PlanAsync(CancellationToken cancellationToken) => Run((r, ct) => r.PlanAsync(ct), cancellationToken);

    public Task<object> ReconcileAsync(CancellationToken cancellationToken) => Run((r, ct) => r.ReconcileAsync(ct), cancellationToken);

    private async Task<object> Run<T>(Func<IReconciler, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        where T : notnull
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<IReconciler>(), cancellationToken);
    }
}
