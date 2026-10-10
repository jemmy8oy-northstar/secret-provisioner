using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services;

/// <summary>
/// Port of <c>src/observe.js</c>. See <see cref="IObserver"/> for the contract.
/// </summary>
/// <remarks>
/// NO VALUE EVER LEAVES THIS CLASS. A Secret arrives with its values in <c>data</c>, right next
/// to the key names we want; reading the keys and reading the values are one character apart.
/// So the value is dropped here, at the earliest possible point.
/// </remarks>
public sealed class Observer : IObserver
{
    // Postgres ships these and they are not ours. Without filtering them out, `template1` reads
    // as a database name already taken, and every `pg_` role looks like a collision with an app.
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.Ordinal) { "postgres", "template0", "template1" };
    private const string SystemRolePrefix = "pg_";

    public IReadOnlyList<string> KeysOf(IKubeSecret? secret)
    {
        // `data` and `stringData` are both possible: a Secret written by kubectl from a literal
        // has `data`, one written from a manifest may have either. Reading only `data` would
        // report a Secret as missing a key it plainly has, and the planner would then plan to
        // patch a key that is already there.
        var names = new HashSet<string>(StringComparer.Ordinal);
        names.UnionWith(secret?.Data?.Keys ?? []);
        names.UnionWith(secret?.StringData?.Keys ?? []);
        return names.Order(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> IndexSecrets(string ns, IKubeSecretList? listing)
    {
        var index = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var secret in listing?.Items ?? [])
        {
            // A Secret with no name cannot be addressed and cannot be what a declaration means.
            // Skipping it is right; silently skipping a NAMESPACE would not be, which is the
            // distinction this whole class turns on.
            var name = secret?.Metadata?.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            index[$"{ns}/{name}"] = KeysOf(secret);
        }

        return index;
    }

    public IPostgresObservation IndexPostgres(IEnumerable<string?> roleNames, IEnumerable<string?> databaseNames) => new PostgresObservation
    {
        Roles = roleNames
            .OfType<string>()
            .Where(n => !n.StartsWith(SystemRolePrefix, StringComparison.Ordinal) && !SystemDatabases.Contains(n))
            .Order(StringComparer.Ordinal)
            .ToList(),
        Databases = databaseNames
            .OfType<string>()
            .Where(n => !SystemDatabases.Contains(n))
            .Order(StringComparer.Ordinal)
            .ToList(),
    };

    public IObservation Observe(IEnumerable<INamespaceListing> results, IPostgresObservation? postgres)
    {
        var namespaces = new List<string>();
        var secrets = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        var failures = new List<NamespaceFailure>();

        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                // Deliberately NOT added to the namespaces. The planner will report every key in
                // it as undecidable, which is loud and correct; adding it would make a failed
                // listing indistinguishable from an empty namespace and turn a transient API
                // error into a destructive plan.
                failures.Add(new NamespaceFailure { Namespace = result.Namespace, Error = result.Error });
                continue;
            }

            namespaces.Add(result.Namespace);
            foreach (var (target, keys) in IndexSecrets(result.Namespace, result.Listing))
            {
                secrets[target] = keys.ToList();
            }
        }

        return new Observation
        {
            Namespaces = namespaces.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Secrets = secrets,
            Postgres = postgres is null ? null : new PostgresObservation { Roles = postgres.Roles?.ToList(), Databases = postgres.Databases?.ToList() },
            Failures = failures,
        };
    }
}
