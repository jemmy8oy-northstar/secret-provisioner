using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// Turns what the Kubernetes API and Postgres actually returned into the observation the
/// planner consumes. Pure: the clients live at the edge and hand their responses in, so
/// "does it ever touch a value" and "does it ever claim to have looked somewhere it didn't"
/// are answerable by a test rather than by an audit of a live cluster.
/// </summary>
/// <remarks>
/// THE CONTRACT WITH THE PLANNER, AND WHY IT IS THE POINT: the planner refuses to plan
/// against a namespace the observation does not list, because "this namespace holds no
/// Secrets" and "I never listed this namespace" are the same empty object — and a failed
/// lookup read as the first plans a CREATE for a Secret that already exists, which deletes
/// every key not in the plan. That refusal is only worth anything if the observer never
/// lies about what it enumerated: a namespace whose listing FAILED must not appear in
/// <see cref="IObservation.Namespaces"/>.
/// </remarks>
public interface IObserver
{
    /// <summary>Key names present in one Secret, sorted — never a value. Reads <c>data</c> and <c>stringData</c>.</summary>
    IReadOnlyList<string> KeysOf(IKubeSecret? secret);

    /// <summary>A namespace's Secret listing, indexed by <c>namespace/name</c>. A Secret with no name is skipped.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> IndexSecrets(string ns, IKubeSecretList? listing);

    /// <summary>Roles and databases that belong to the estate, from <c>pg_roles.rolname</c> and <c>pg_database.datname</c>.</summary>
    IPostgresObservation IndexPostgres(IEnumerable<string?> roleNames, IEnumerable<string?> databaseNames);

    /// <summary>
    /// Assemble the observation. One listing per namespace ATTEMPTED; one carrying an error
    /// contributes nothing, and above all its name does not go into the namespaces.
    /// <paramref name="postgres"/> null means the server was not inspected.
    /// </summary>
    IObservation Observe(IEnumerable<INamespaceListing> results, IPostgresObservation? postgres);
}
