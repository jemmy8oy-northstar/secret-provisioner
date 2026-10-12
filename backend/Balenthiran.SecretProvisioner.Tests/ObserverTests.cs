using System.Text.Json;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// Ported from <c>src/observe.test.js</c>, then made create-only (secret-provisioner#9): the observer
/// assembles dry-run probes, not Secret listings. Plus the seam into the planner, from both sides.
/// </summary>
public class ObserverTests
{
    private readonly Observer observer = new();

    private static SecretProbe Exists(string ns, string name) => new() { Namespace = ns, Name = name, Exists = true };

    private static SecretProbe Absent(string ns, string name) => new() { Namespace = ns, Name = name, Exists = false };

    private static SecretProbe Failed(string ns, string name, string error) => new() { Namespace = ns, Name = name, Error = error };

    private static PostgresObservation NoPg() => new() { Roles = [], Databases = [] };

    private static readonly SecretDeclaration AroundTheWorld = new()
    {
        App = "around-the-world",
        Namespace = "balenthiran",
        SecretName = "around-the-world-secrets",
        Keys = [new KeyDeclaration { Key = "Jwt__Secret", Type = "random" }],
    };

    [Fact]
    public void An_existing_Secret_is_recorded_as_existing_and_unread()
    {
        var obs = observer.Observe([Exists("balenthiran", "around-the-world-secrets"), Absent("balenthiran", "kit-secrets")], NoPg());

        Assert.Equal(["balenthiran"], obs.Namespaces!);
        var (target, keys) = Assert.Single(obs.Secrets!);
        Assert.Equal("balenthiran/around-the-world-secrets", target);
        Assert.Null(keys);
    }

    [Fact]
    public void A_namespace_with_only_absent_Secrets_is_enumerated_with_no_secrets()
    {
        var obs = observer.Observe([Absent("balenthiran", "x")], NoPg());
        Assert.Equal(["balenthiran"], obs.Namespaces!);
        Assert.Empty(obs.Secrets!);
    }

    [Fact]
    public void A_namespace_with_a_failed_probe_is_not_claimed_as_enumerated()
    {
        var obs = observer.Observe([Failed("balenthiran", "around-the-world-secrets", "connect ETIMEDOUT"), Absent("other", "x")], NoPg());

        // If `balenthiran` appeared here, the planner would read "around-the-world-secrets does
        // not exist" and plan to CREATE it over the live one.
        Assert.Equal(["other"], obs.Namespaces!);
        Assert.Empty(obs.Secrets!);
        var failure = Assert.Single(obs.Failures!);
        Assert.Equal(("balenthiran", "connect ETIMEDOUT"), (failure.Namespace, failure.Error));
    }

    [Fact]
    public void One_failed_probe_takes_the_whole_namespace_out_even_if_its_neighbours_answered()
    {
        var obs = observer.Observe(
            [Exists("balenthiran", "kit-secrets"), Failed("balenthiran", "around-the-world-secrets", "503"), Absent("balenthiran", "macro-metrics-secrets")],
            NoPg());

        Assert.Empty(obs.Namespaces!);
        Assert.Empty(obs.Secrets!);
        Assert.Equal("503", Assert.Single(obs.Failures!).Error);
    }

    [Fact]
    public void Two_failures_in_one_namespace_are_reported_once()
    {
        var obs = observer.Observe([Failed("balenthiran", "a", "403"), Failed("balenthiran", "b", "403")], null);
        Assert.Single(obs.Failures!);
    }

    [Fact]
    public void Every_probe_failing_yields_an_honest_observation()
    {
        var obs = observer.Observe([Failed("balenthiran", "x", "403")], null);
        Assert.Empty(obs.Namespaces!);
        Assert.Null(obs.Postgres);
        Assert.Single(obs.Failures!);
    }

    [Fact]
    public void An_empty_error_message_still_counts_as_a_failure()
    {
        var obs = observer.Observe([Failed("balenthiran", "x", string.Empty)], null);
        Assert.Empty(obs.Namespaces!);
        Assert.Single(obs.Failures!);
    }

    [Fact]
    public void A_probe_with_neither_answer_nor_error_is_a_failure_not_an_absence()
    {
        var obs = observer.Observe([new SecretProbe { Namespace = "balenthiran", Name = "x" }], null);
        Assert.Empty(obs.Namespaces!);
        Assert.Single(obs.Failures!);
    }

    [Fact]
    public void System_roles_and_databases_are_not_mistaken_for_ours()
    {
        var pg = observer.IndexPostgres(["postgres", "pg_read_all_data", "aroundtheworld"], ["template0", "template1", "postgres", "aroundtheworld"]);
        Assert.Equal(["aroundtheworld"], pg.Roles!);
        Assert.Equal(["aroundtheworld"], pg.Databases!);
    }

    [Fact]
    public void An_empty_catalogue_is_empty_not_an_error()
    {
        var pg = observer.IndexPostgres([], []);
        Assert.Empty(pg.Roles!);
        Assert.Empty(pg.Databases!);
    }

    [Fact]
    public void Postgres_null_travels_through_untouched() =>
        Assert.Null(observer.Observe([Absent("balenthiran", "x")], null).Postgres);

    [Fact]
    public void The_observation_is_exactly_what_the_planner_demands()
    {
        var json = JsonSerializer.SerializeToNode((Observation)observer.Observe([Exists("balenthiran", "s")], new PostgresObservation { Roles = ["r"], Databases = ["d"] }), Json.Options)!.AsObject();
        Assert.Equal(["failures", "namespaces", "postgres", "secrets"], json.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Duplicate_namespaces_collapse() =>
        Assert.Equal(["balenthiran"], observer.Observe([Exists("balenthiran", "a"), Absent("balenthiran", "b")], null).Namespaces!);

    [Fact]
    public void A_failed_probe_reaches_the_planner_as_undecidable_never_as_a_create()
    {
        // The seam from both sides: what the observer omits, the planner refuses to act on.
        var obs = observer.Observe([Failed("balenthiran", "around-the-world-secrets", "connect ETIMEDOUT")], NoPg());

        var plan = new Planner().Plan([AroundTheWorld], obs);
        Assert.Empty(plan.Steps);
        Assert.Equal("Jwt__Secret", Assert.Single(plan.Unknown).Key);
    }

    [Fact]
    public void An_existing_Secret_reaches_the_planner_as_left_alone_never_as_a_write()
    {
        // The other side: "exists, unread" must plan nothing — not a create (which the API would
        // refuse) and not a write of the missing key (which create-only has no verb for).
        var obs = observer.Observe([Exists("balenthiran", "around-the-world-secrets")], NoPg());

        var plan = new Planner().Plan([AroundTheWorld], obs);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.Blocked);
        Assert.Equal("Jwt__Secret", Assert.Single(plan.Noop).Key);
    }

    [Fact]
    public void An_absent_Secret_reaches_the_planner_as_a_create()
    {
        var obs = observer.Observe([Absent("balenthiran", "around-the-world-secrets")], NoPg());

        var step = Assert.Single(new Planner().Plan([AroundTheWorld], obs).Steps);
        Assert.Equal(("create-secret", "around-the-world-secrets"), (step.Kind, step.SecretName));
    }
}
