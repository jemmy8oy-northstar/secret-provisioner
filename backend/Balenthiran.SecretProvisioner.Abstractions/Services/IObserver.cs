using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// Turns what the Kubernetes API and Postgres actually returned into the observation the
/// planner consumes. Pure: the clients live at the edge and hand their responses in, so
/// "does it ever claim to have looked somewhere it didn't" is answerable by a test rather than
/// by an audit of a live cluster.
/// </summary>
/// <remarks>
/// THE CONTRACT WITH THE PLANNER, AND WHY IT IS THE POINT: the planner refuses to plan
/// against a namespace the observation does not list, because "this Secret does not exist" and
/// "I could not ask" must never be the same answer — a failed probe read as the first plans a
/// CREATE for a Secret that already exists. So a namespace in which ANY probe failed must not
/// appear in <see cref="IObservation.Namespaces"/>.
/// </remarks>
public interface IObserver
{
    /// <summary>Roles and databases that belong to the estate, from <c>pg_roles.rolname</c> and <c>pg_database.datname</c>.</summary>
    IPostgresObservation IndexPostgres(IEnumerable<string?> roleNames, IEnumerable<string?> databaseNames);

    /// <summary>
    /// Assemble the observation. A Secret that exists goes into <see cref="IObservation.Secrets"/>
    /// with a null key list — it exists and was not read. A namespace with any failed probe
    /// contributes nothing, and above all its name does not go into the namespaces.
    /// <paramref name="postgres"/> null means the server was not inspected.
    /// </summary>
    IObservation Observe(IEnumerable<ISecretProbe> probes, IPostgresObservation? postgres);
}
