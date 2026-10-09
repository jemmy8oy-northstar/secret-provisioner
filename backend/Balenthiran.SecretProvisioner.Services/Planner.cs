using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services;

/// <summary>
/// Port of <c>oke-fleet:scripts/plan-secrets.mjs</c> (oke-fleet#9), scored against that file's
/// own output in <c>Tests/Fixtures/Plan</c>. See <see cref="IPlanner"/> for the rules.
/// </summary>
/// <remarks>
/// THE FACT THAT BREAKS THE OBVIOUS IMPLEMENTATION: <c>around-the-world-secrets</c> holds FIVE
/// keys. Writing that Secret with the one key being provisioned DELETES the other four, and two
/// of them are <c>Jwt__Secret</c> and <c>Admin__Key</c> — so the symptom is every logged-in
/// guest signed out mid-party, not an error. A Secret that already exists is therefore only
/// ever PATCHED, one key at a time, and <see cref="AssertNeverClobbers"/> is the invariant, not
/// a comment about one.
/// </remarks>
public sealed class Planner : IPlanner
{
    public const int DefaultRandomBytes = 32;

    public IPlan Plan(IReadOnlyList<ISecretDeclaration> declarations, IObservation observation)
    {
        var state = Normalise(observation);

        var ddl = new List<PlanStep>();
        var blocked = new List<PlanEntry>();
        var unknown = new List<PlanEntry>();
        var drift = new List<PlanEntry>();
        var unmanaged = new List<PlanEntry>();
        var noop = new List<PlanEntry>();

        // (namespace/secretName) -> keys this run would write into it, in first-seen order.
        var writes = new List<(string Target, List<(IKeyDeclaration Entry, string Source)> Keys)>();

        // Postgres identifiers this run has already planned to create. Two declarations may not
        // claim one role — oke-fleet's validator rejects that — but the planner must not depend
        // on having been run after the validator.
        var plannedRoles = new HashSet<string>(StringComparer.Ordinal);
        var plannedDatabases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decl in declarations)
        {
            var target = $"{decl.Namespace}/{decl.SecretName}";

            // Did we actually look here? This is the difference between "create it" and "I have
            // no idea whether it exists".
            var enumerated = state.Namespaces.Contains(decl.Namespace);
            var existing = state.Secrets.GetValueOrDefault(target);

            PlanEntry At(IKeyDeclaration entry, string reason, string? action = null) => new()
            {
                App = decl.App,
                Namespace = decl.Namespace,
                SecretName = decl.SecretName,
                Key = entry.Key,
                Type = entry.Type,
                Action = action,
                Reason = reason,
            };

            foreach (var entry in decl.Keys)
            {
                if (!enumerated)
                {
                    unknown.Add(At(entry, $"namespace {decl.Namespace} was not enumerated — planning a create here could overwrite a Secret that already exists"));
                    continue;
                }

                if (existing is not null && existing.Contains(entry.Key))
                {
                    // Create-only: the value stays untouched. But a present Postgres key whose role
                    // does not exist is a connection string that cannot authenticate, and nothing
                    // else in this estate would ever notice.
                    if (entry.Type == "postgres-db" && state.Postgres is not null && !state.Postgres.Value.Roles.Contains(entry.Role!))
                    {
                        drift.Add(At(entry, $"key is present but its Postgres role {entry.Role} does not exist — this connection string cannot authenticate"));
                    }
                    else
                    {
                        noop.Add(At(entry, "already present — create-only leaves it alone"));
                    }

                    continue;
                }

                switch (entry.Type)
                {
                    case "external":
                        blocked.Add(At(
                            entry,
                            $"nothing can generate this value; a human must put it in {target}#{entry.Key}. Left as it is, the pod fails with CreateContainerConfigError minutes after it starts",
                            "supply-externally"));
                        continue;

                    case "random":
                        var bytes = entry.Bytes ?? DefaultRandomBytes;
                        AddWrite(writes, target, entry, $"generate {bytes} random bytes, hex-encoded (openssl rand -hex {bytes})");
                        continue;

                    case "postgres-db":
                        if (state.Postgres is null)
                        {
                            unknown.Add(At(entry, "the Postgres server was not inspected — whether role and database already exist decides this entirely"));
                            continue;
                        }

                        var role = entry.Role!;
                        var database = entry.Database!;
                        var roleExists = state.Postgres.Value.Roles.Contains(role) || plannedRoles.Contains(role);
                        var dbExists = state.Postgres.Value.Databases.Contains(database) || plannedDatabases.Contains(database);

                        // The password is the whole difficulty. A role that already exists has a
                        // password we do not know and may not change: create-only forbids it, and an
                        // ALTER ROLE would instantly break whoever authenticates with the current one.
                        // So there is no connection string we can honestly compose — say so.
                        if (roleExists)
                        {
                            blocked.Add(At(
                                entry,
                                $"Postgres role {role} already exists, so its password is neither knowable nor ours to change (an ALTER ROLE would break whatever authenticates with it now). Compose the connection string by hand, or drop the role deliberately if nothing uses it",
                                "rotate-by-hand"));
                            continue;
                        }

                        if (dbExists)
                        {
                            blocked.Add(At(
                                entry,
                                $"database {database} already exists but role {role} does not, so it is owned by someone else — create-only cannot reassign an owner",
                                "reassign-by-hand"));
                            continue;
                        }

                        plannedRoles.Add(role);
                        plannedDatabases.Add(database);
                        ddl.Add(DdlStep("create-role", decl, entry, null, role, $"CREATE ROLE {role} LOGIN PASSWORD <generated>"));
                        ddl.Add(DdlStep("create-database", decl, entry, database, role, $"CREATE DATABASE {database} OWNER {role}"));
                        AddWrite(writes, target, entry, $"the Npgsql connection string for {database} as {role}");
                        continue;

                    default:
                        // Unreachable while the schema's types and this switch agree. It is here
                        // because they are edited in different repos: a fourth type added to the
                        // schema without a branch here must stop the plan, not fall through it as a
                        // silent noop.
                        unknown.Add(At(entry, $"no planner branch for type {JsQuote(entry.Type)} — the schema knows a type this planner does not"));
                        continue;
                }
            }

            // Keys sitting in a Secret this estate declares, that no declaration claims. Never
            // deleted; only named, so an unexplained one is visible rather than quietly
            // load-bearing.
            if (existing is not null)
            {
                var declared = decl.Keys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var key in existing.Order(StringComparer.Ordinal).Where(k => !declared.Contains(k)))
                {
                    unmanaged.Add(new PlanEntry
                    {
                        App = decl.App,
                        Namespace = decl.Namespace,
                        SecretName = decl.SecretName,
                        Key = key,
                        Reason = "present in the cluster but declared nowhere — left untouched",
                    });
                }
            }
        }

        // Postgres DDL first: a connection string cannot be composed before the role and
        // database it names exist.
        var steps = new List<PlanStep>(ddl);

        foreach (var (target, keys) in writes)
        {
            var existing = state.Secrets.GetValueOrDefault(target);
            var slash = target.IndexOf('/'); // a Secret name may not contain a slash
            var ns = target[..slash];
            var secretName = target[(slash + 1)..];

            if (existing is null)
            {
                steps.Add(new PlanStep
                {
                    Kind = "create-secret",
                    Namespace = ns,
                    SecretName = secretName,
                    Keys = keys.Select(k => new StepKey { Key = k.Entry.Key, Type = k.Entry.Type, Source = k.Source }).ToList(),
                    Detail = $"create Secret {target} with {keys.Count} key(s)",
                });
                continue;
            }

            // THE invariant. One step per key, never a write of the whole Secret, and each step
            // names what it must not disturb.
            foreach (var (entry, source) in keys)
            {
                steps.Add(new PlanStep
                {
                    Kind = "patch-secret-key",
                    Namespace = ns,
                    SecretName = secretName,
                    Key = entry.Key,
                    Type = entry.Type,
                    Source = source,
                    Preserves = existing.Order(StringComparer.Ordinal).ToList(),
                    Detail = $"patch {target}#{entry.Key}, preserving {existing.Count} existing key(s)",
                });
            }
        }

        var plan = new Plan
        {
            Steps = steps,
            Blocked = blocked,
            Unknown = unknown,
            Drift = drift,
            Unmanaged = unmanaged,
            Noop = noop,
            Summary = new PlanSummary
            {
                Steps = steps.Count,
                CreateSecret = steps.Count(s => s.Kind == "create-secret"),
                PatchKey = steps.Count(s => s.Kind == "patch-secret-key"),
                CreateRole = steps.Count(s => s.Kind == "create-role"),
                CreateDatabase = steps.Count(s => s.Kind == "create-database"),
                Blocked = blocked.Count,
                Unknown = unknown.Count,
                Drift = drift.Count,
                Unmanaged = unmanaged.Count,
                Noop = noop.Count,
            },
        };

        AssertNeverClobbers(plan, observation);
        return plan;
    }

    /// <summary>
    /// The one property the planner exists to guarantee: no step ever creates a Secret that
    /// already exists. Checked rather than intended, because getting it wrong is silent and
    /// user-visible while the code path that would do it looks perfectly reasonable in a diff.
    /// Public because it is unreachable through <see cref="Plan"/> by construction, and a guard
    /// no test can execute is indistinguishable from one that does not work.
    /// </summary>
    public void AssertNeverClobbers(IPlan plan, IObservation observation)
    {
        var secrets = Normalise(observation).Secrets;
        foreach (var step in plan.Steps.Where(s => s.Kind == "create-secret"))
        {
            var target = $"{step.Namespace}/{step.SecretName}";
            if (secrets.TryGetValue(target, out var keys))
            {
                throw new InvalidOperationException($"planner bug: planned to create {target}, which already exists with {keys.Count} key(s) — that would delete every key not in this plan");
            }
        }
    }

    private static PlanStep DdlStep(string kind, ISecretDeclaration decl, IKeyDeclaration entry, string? database, string role, string detail) => new()
    {
        Kind = kind,
        App = decl.App,
        Namespace = decl.Namespace,
        SecretName = decl.SecretName,
        Key = entry.Key,
        Type = entry.Type,
        Database = database,
        Role = role,
        Detail = detail,
    };

    private static void AddWrite(List<(string Target, List<(IKeyDeclaration Entry, string Source)> Keys)> writes, string target, IKeyDeclaration entry, string source)
    {
        var slot = writes.FindIndex(w => w.Target == target);
        if (slot < 0)
        {
            writes.Add((target, []));
            slot = writes.Count - 1;
        }

        writes[slot].Keys.Add((entry, source));
    }

    // JSON.stringify of a string, which is how the JS planner quoted an unknown type.
    private static string JsQuote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static NormalisedObservation Normalise(IObservation observed)
    {
        if (observed.Namespaces is null)
        {
            throw new InvalidObservationException("observation must list the namespaces it enumerated — an unlisted namespace is \"I did not look\", not \"it is empty\"");
        }

        var secrets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (target, keys) in observed.Secrets ?? new Dictionary<string, IReadOnlyList<string>?>())
        {
            if (keys is null)
            {
                throw new InvalidObservationException($"observation: secrets[{JsQuote(target)}] must be an array of key names");
            }

            secrets[target] = keys.ToHashSet(StringComparer.Ordinal);
        }

        var pg = observed.Postgres;
        if (pg is not null && (pg.Roles is null || pg.Databases is null))
        {
            throw new InvalidObservationException("observation: postgres must be null (not inspected) or {roles: [], databases: []}");
        }

        return new NormalisedObservation(
            observed.Namespaces.ToHashSet(StringComparer.Ordinal),
            secrets,
            pg is null ? null : (pg.Roles!.ToHashSet(StringComparer.Ordinal), pg.Databases!.ToHashSet(StringComparer.Ordinal)));
    }

    private sealed record NormalisedObservation(
        HashSet<string> Namespaces,
        Dictionary<string, HashSet<string>> Secrets,
        (HashSet<string> Roles, HashSet<string> Databases)? Postgres);
}
