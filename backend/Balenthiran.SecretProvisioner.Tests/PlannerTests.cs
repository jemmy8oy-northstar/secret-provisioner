using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// The planner's properties that a golden cannot hold, because they are about every plan rather
/// than one: the clobber guard, purity, and that no key is ever silently dropped.
/// </summary>
public class PlannerTests
{
    // secrets/around-the-world.json, verbatim. Five keys: one Postgres, two random, two that
    // nothing can generate.
    private static SecretDeclaration AroundTheWorld() => new()
    {
        App = "around-the-world",
        Namespace = "balenthiran",
        SecretName = "around-the-world-secrets",
        Keys =
        [
            new() { Key = "ConnectionStrings__DefaultConnection", Type = "postgres-db", Database = "aroundtheworld", Role = "aroundtheworld" },
            new() { Key = "Jwt__Secret", Type = "random", Bytes = 32 },
            new() { Key = "Admin__Key", Type = "random", Bytes = 16 },
            new() { Key = "PhotoStorage__AccessKeyId", Type = "external" },
            new() { Key = "PhotoStorage__SecretAccessKey", Type = "external" },
        ],
    };

    private static Observation Live(params string[] keys) => new()
    {
        Namespaces = ["balenthiran"],
        Secrets = new() { ["balenthiran/around-the-world-secrets"] = [.. keys] },
        Postgres = new PostgresObservation { Roles = [], Databases = [] },
    };

    [Fact]
    public void The_clobber_guard_rejects_a_plan_that_creates_over_an_existing_secret()
    {
        // Unreachable through Plan by construction, so it is called with a plan built to be wrong.
        var bug = Assert.Throws<InvalidOperationException>(() => new Planner().AssertNeverClobbers(Creating("around-the-world-secrets"), Live("A", "B", "C", "D")));
        Assert.Equal("planner bug: planned to create balenthiran/around-the-world-secrets, which already exists with 4 key(s) — that would delete every key not in this plan", bug.Message);
    }

    [Fact]
    public void The_clobber_guard_passes_a_create_for_a_secret_that_does_not_exist() =>
        new Planner().AssertNeverClobbers(Creating("new-secrets"), Live("A"));

    private static Plan Creating(string secretName) => new()
    {
        Steps = [new PlanStep { Kind = "create-secret", Namespace = "balenthiran", SecretName = secretName, Detail = "x" }],
        Blocked = [],
        Unknown = [],
        Drift = [],
        Unmanaged = [],
        Noop = [],
        Summary = new PlanSummary { Steps = 1, CreateSecret = 1, PatchKey = 0, CreateRole = 0, CreateDatabase = 0, Blocked = 0, Unknown = 0, Drift = 0, Unmanaged = 0, Noop = 0 },
    };

    [Fact]
    public void An_existing_secret_is_patched_and_never_created_whatever_it_holds()
    {
        // Every subset of the five keys being present: none may produce a create-secret.
        var all = AroundTheWorld().Keys.Select(k => k.Key).ToArray();
        for (var mask = 0; mask < 1 << all.Length; mask++)
        {
            var present = all.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            var plan = new Planner().Plan([AroundTheWorld()], Live(present));
            Assert.DoesNotContain(plan.Steps, s => s.Kind == "create-secret");
            Assert.All(plan.Steps.Where(s => s.Kind == "patch-secret-key"), s => Assert.Equal(present.Order(StringComparer.Ordinal), s.Preserves!));
        }
    }

    [Fact]
    public void Planning_is_pure_the_same_input_twice_gives_identical_plans()
    {
        var declarations = new[] { AroundTheWorld() };
        var observation = Live("Admin__Key");
        var first = System.Text.Json.JsonSerializer.Serialize((Plan)new Planner().Plan(declarations, observation), Json.Options);
        var second = System.Text.Json.JsonSerializer.Serialize((Plan)new Planner().Plan(declarations, observation), Json.Options);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Every_declared_key_lands_in_exactly_one_bucket()
    {
        var observations = new[]
        {
            new Observation { Namespaces = ["balenthiran"], Secrets = [], Postgres = new PostgresObservation { Roles = [], Databases = [] } },
            Live("Admin__Key", "PhotoStorage__AccessKeyId"),
            new Observation { Namespaces = [], Secrets = [] },
            new Observation { Namespaces = ["balenthiran"], Secrets = [], Postgres = null },
            new Observation { Namespaces = ["balenthiran"], Secrets = [], Postgres = new PostgresObservation { Roles = ["aroundtheworld"], Databases = [] } },
        };

        foreach (var observation in observations)
        {
            var plan = new Planner().Plan([AroundTheWorld()], observation);
            var landed = Landed(plan);
            Assert.Equal(AroundTheWorld().Keys.Select(k => k.Key).Order(StringComparer.Ordinal), landed.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void The_planner_never_plans_a_delete_in_any_shape()
    {
        var plan = new Planner().Plan([AroundTheWorld()], Live("Admin__Key", "Stray"));
        Assert.All(plan.Steps, s => Assert.Contains(s.Kind, new[] { "create-role", "create-database", "create-secret", "patch-secret-key" }));
        Assert.Contains(plan.Unmanaged, e => e.Key == "Stray");
    }

    // Each declared key, once per bucket or step it appears in. A create-role/create-database
    // pair is one key's DDL, so steps count only the writes.
    private static List<string> Landed(IPlan plan) =>
    [
        .. plan.Steps.Where(s => s.Kind == "patch-secret-key").Select(s => s.Key!),
        .. plan.Steps.Where(s => s.Kind == "create-secret").SelectMany(s => s.Keys!).Select(k => k.Key),
        .. plan.Blocked.Select(e => e.Key),
        .. plan.Unknown.Select(e => e.Key),
        .. plan.Drift.Select(e => e.Key),
        .. plan.Noop.Select(e => e.Key),
    ];
}
