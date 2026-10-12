using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services;

/// <summary>
/// Ported from <c>src/observe.js</c>, then made create-only (secret-provisioner#9). See <see cref="IObserver"/> for the contract.
/// </summary>
/// <remarks>
/// NO VALUE EVER REACHES THIS CLASS. Under create-only (secret-provisioner#9) the cluster never
/// sends one: a probe answers "exists" or "does not", and nothing else about the Secret.
/// </remarks>
public sealed class Observer : IObserver
{
    // Postgres ships these and they are not ours. Without filtering them out, `template1` reads
    // as a database name already taken, and every `pg_` role looks like a collision with an app.
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.Ordinal) { "postgres", "template0", "template1" };
    private const string SystemRolePrefix = "pg_";

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

    public IObservation Observe(IEnumerable<ISecretProbe> probes, IPostgresObservation? postgres)
    {
        var all = probes.ToList();

        // A namespace is enumerated only if EVERY probe in it answered. One failure and the whole
        // namespace is undecidable: the planner reports each of its keys as unknown, which is loud
        // and correct. Keeping the namespace because some probes worked would let a failed probe
        // read as "absent", and turn a transient API error into a create over a live Secret.
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var probe in all.Where(p => p.Exists is null))
        {
            failed.TryAdd(probe.Namespace, probe.Error ?? "the probe returned neither an answer nor an error");
        }

        var secrets = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        foreach (var probe in all.Where(p => p.Exists == true && !failed.ContainsKey(p.Namespace)))
        {
            // Exists, contents not read — and not readable: the Role holds no get or list.
            secrets[$"{probe.Namespace}/{probe.Name}"] = null;
        }

        return new Observation
        {
            Namespaces = all.Select(p => p.Namespace).Where(ns => !failed.ContainsKey(ns)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            Secrets = secrets,
            Postgres = postgres is null ? null : new PostgresObservation { Roles = postgres.Roles?.ToList(), Databases = postgres.Databases?.ToList() },
            Failures = failed.Select(f => new NamespaceFailure { Namespace = f.Key, Error = f.Value }).ToList(),
        };
    }
}
