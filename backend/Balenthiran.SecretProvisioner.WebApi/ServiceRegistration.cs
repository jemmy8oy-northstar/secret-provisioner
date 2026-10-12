using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.Services.Clients;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using IRouter = Balenthiran.SecretProvisioner.Abstractions.Services.IRouter;

namespace Balenthiran.SecretProvisioner.WebApi;

/// <summary>
/// Wires the clients, the decisions and the router. Every registration is a TryAdd, so a test
/// registers a fake BEFORE these and scores the rest of the wiring around it.
/// </summary>
/// <remarks>
/// THE RECONCILER IS SCOPED — ONE PER REQUEST — BECAUSE THE SERVICE-ACCOUNT TOKEN ROTATES. The
/// kubelet rewrites the projected token file while the pod runs, and a client holding the token it
/// read at start would one day be refused with no change anyone made. So each request reads the
/// token afresh; the connection itself (host, CA, sockets) is one per process.
///
/// The router is a SINGLETON, and must be: its "a reconcile is already running" flag is the one
/// piece of state shared between requests. A router per request would make that guard exist, pass
/// its own tests, and never once be true.
/// </remarks>
public static class ServiceRegistration
{
    public static IServiceCollection AddProvisionerServices(this IServiceCollection services, ProvisionerSettings settings)
    {
        services.AddSingleton(settings);
        services.TryAddSingleton<IObserver, Observer>();
        services.TryAddSingleton<IPlanner, Planner>();
        services.TryAddSingleton<IDeclarationParser, DeclarationParser>();

        services.TryAddSingleton<IDeclarationSource>(sp => new GitHubDeclarationSource(
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
            sp.GetRequiredService<IDeclarationParser>()));

        services.TryAddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString));
        services.TryAddSingleton<IPostgresClient>(sp => new PostgresClient(sp.GetRequiredService<NpgsqlDataSource>()));

        // Built on first use, not at start: outside a cluster there is no service account, and
        // the process should still start far enough to say so on the first request.
        services.TryAddSingleton(_ => KubernetesHttpClientFactory.Create(InClusterConfig.FromEnvironment()));
        services.TryAddScoped<IKubernetesClient>(sp => new KubernetesClient(
            sp.GetRequiredService<HttpClient>(),
            InClusterConfig.FromEnvironment().Token));

        services.TryAddScoped<IExecutor>(sp => new Executor(sp.GetRequiredService<IKubernetesClient>(), sp.GetRequiredService<IPostgresClient>()));
        services.TryAddSingleton<IPostgresEndpoint>(new PostgresEndpoint { Host = settings.AppHost, Port = settings.AppPort });
        services.TryAddScoped<IReconciler, Reconciler>();

        services.TryAddSingleton<IProvisionerHandlers, ScopedHandlers>();
        services.TryAddSingleton<IRouter, Router>();
        return services;
    }
}
