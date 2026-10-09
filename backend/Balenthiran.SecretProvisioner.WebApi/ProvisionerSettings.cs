using Npgsql;

namespace Balenthiran.SecretProvisioner.WebApi;

/// <summary>
/// Everything the process reads from its environment, read once at start so a missing value
/// stops the pod before it serves anything rather than on the first reconcile.
/// </summary>
/// <remarks>
/// THE OWN CREDENTIAL IS ONE CONNECTION STRING in <c>ConnectionStrings__DefaultConnection</c>,
/// injected from a Kubernetes Secret — the shape every app in the fleet already uses, and the
/// stated default on secret-provisioner#9. It is the one credential this service cannot create for
/// itself.
///
/// THE HOST THE APPS ARE GIVEN IS THE HOST THIS POD DIALS. A composed connection string names
/// where an app reaches Postgres, and an in-cluster app reaches it at the same service DNS name
/// this pod does. Deriving it means there is no second setting that can disagree with the first.
/// </remarks>
public sealed class ProvisionerSettings
{
    public const string ConnectionStringVariable = "ConnectionStrings__DefaultConnection";

    private ProvisionerSettings(string connectionString, string appHost, int appPort)
    {
        ConnectionString = connectionString;
        AppHost = appHost;
        AppPort = appPort;
    }

    /// <summary>This pod's own Postgres connection. A secret: never logged, never in <see cref="ToString"/>.</summary>
    public string ConnectionString { get; }

    /// <summary>The Postgres host written into the connection strings this service composes.</summary>
    public string AppHost { get; }

    public int AppPort { get; }

    public static ProvisionerSettings FromEnvironment(Func<string, string?> env)
    {
        var connectionString = env(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionStringVariable} is not set — the provisioner's own Postgres connection is the one credential it cannot create for itself");
        }

        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Not the parser's message: it can quote the fragment it choked on, and the
            // fragment can be the password.
            throw new InvalidOperationException($"{ConnectionStringVariable} is not a valid Npgsql connection string");
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            throw new InvalidOperationException($"{ConnectionStringVariable} names no Host");
        }

        return new ProvisionerSettings(connectionString, parsed.Host, parsed.Port);
    }

    public override string ToString() => $"postgres {AppHost}:{AppPort}";
}
