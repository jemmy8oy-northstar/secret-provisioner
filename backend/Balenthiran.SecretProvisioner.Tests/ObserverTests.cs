using System.Text.Json;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>Port of <c>src/observe.test.js</c>, case for case, plus the seam into the planner.</summary>
public class ObserverTests
{
    private readonly Observer observer = new();

    // A Secret shaped the way the Kubernetes API actually returns one — values present,
    // base64-encoded, sitting immediately beside the names we want.
    private static KubeSecret LiveSecret() => new()
    {
        Metadata = new KubeObjectMeta { Name = "around-the-world-secrets", Namespace = "balenthiran" },
        Data = new()
        {
            ["ConnectionStrings__DefaultConnection"] = "SG9zdD1wZztQYXNzd29yZD1odW50ZXIy",
            ["Jwt__Secret"] = "ZGVhZGJlZWZkZWFkYmVlZg==",
            ["Admin__Key"] = "YWRtaW4ta2V5",
        },
    };

    private static NamespaceListing Listed(string ns, params KubeSecret[] items) => new() { Namespace = ns, Listing = new KubeSecretList { Items = [.. items] } };

    private static PostgresObservation NoPg() => new() { Roles = [], Databases = [] };

    [Fact]
    public void Reads_key_names_and_nothing_else() =>
        Assert.Equal(["Admin__Key", "ConnectionStrings__DefaultConnection", "Jwt__Secret"], observer.KeysOf(LiveSecret()));

    [Fact]
    public void No_value_survives_the_observer_in_any_shape()
    {
        var secret = LiveSecret();
        var serialised = JsonSerializer.Serialize((Observation)observer.Observe([Listed("balenthiran", secret)], NoPg()), Json.Options);

        foreach (var value in secret.Data!.Values)
        {
            Assert.DoesNotContain(value!, serialised);
        }

        // …and it did carry the names, so the test above is not passing by emptiness.
        Assert.Contains("Jwt__Secret", serialised);
    }

    [Fact]
    public void StringData_counts_as_present_not_missing()
    {
        var secret = new KubeSecret { Metadata = new KubeObjectMeta { Name = "s" }, Data = new() { ["A"] = "eA==" }, StringData = new() { ["B"] = "plain" } };
        Assert.Equal(["A", "B"], observer.KeysOf(secret));
    }

    [Fact]
    public void A_secret_with_no_name_is_skipped_and_the_namespace_still_counts_as_read()
    {
        var obs = observer.Observe([Listed("balenthiran", new KubeSecret { Metadata = new KubeObjectMeta() }, LiveSecret())], NoPg());
        Assert.Equal(["balenthiran/around-the-world-secrets"], obs.Secrets!.Keys);
        Assert.Equal(["balenthiran"], obs.Namespaces!);
    }

    [Fact]
    public void An_empty_namespace_is_enumerated_with_no_secrets()
    {
        var obs = observer.Observe([Listed("balenthiran")], NoPg());
        Assert.Equal(["balenthiran"], obs.Namespaces!);
        Assert.Empty(obs.Secrets!);
    }

    [Fact]
    public void A_namespace_whose_listing_failed_is_not_claimed_as_enumerated()
    {
        var obs = observer.Observe([new NamespaceListing { Namespace = "balenthiran", Error = "connect ETIMEDOUT" }, Listed("other")], NoPg());

        // If `balenthiran` appeared here, the planner would read "no Secrets in balenthiran" and
        // plan to CREATE around-the-world-secrets over the live one, deleting the four keys it is
        // not writing.
        Assert.Equal(["other"], obs.Namespaces!);
        Assert.Empty(obs.Secrets!);
        var failure = Assert.Single(obs.Failures!);
        Assert.Equal(("balenthiran", "connect ETIMEDOUT"), (failure.Namespace, failure.Error));
    }

    [Fact]
    public void Every_namespace_failing_yields_an_honest_observation()
    {
        var obs = observer.Observe([new NamespaceListing { Namespace = "balenthiran", Error = "403" }], null);
        Assert.Empty(obs.Namespaces!);
        Assert.Null(obs.Postgres);
        Assert.Single(obs.Failures!);
    }

    [Fact]
    public void An_empty_error_message_still_counts_as_a_failure()
    {
        var obs = observer.Observe([new NamespaceListing { Namespace = "balenthiran", Error = string.Empty }], null);
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
        Assert.Null(observer.Observe([Listed("balenthiran")], null).Postgres);

    [Fact]
    public void The_observation_is_exactly_what_the_planner_demands()
    {
        var json = JsonSerializer.SerializeToNode((Observation)observer.Observe([Listed("balenthiran", LiveSecret())], new PostgresObservation { Roles = ["r"], Databases = ["d"] }), Json.Options)!.AsObject();
        Assert.Equal(["failures", "namespaces", "postgres", "secrets"], json.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Duplicate_namespaces_collapse() =>
        Assert.Equal(["balenthiran"], observer.Observe([Listed("balenthiran", LiveSecret()), Listed("balenthiran")], null).Namespaces!);

    [Fact]
    public void A_failed_listing_reaches_the_planner_as_undecidable_never_as_a_create()
    {
        // The seam from both sides: what the observer omits, the planner refuses to act on.
        var declaration = new SecretDeclaration
        {
            App = "around-the-world",
            Namespace = "balenthiran",
            SecretName = "around-the-world-secrets",
            Keys = [new KeyDeclaration { Key = "Jwt__Secret", Type = "random" }],
        };
        var obs = observer.Observe([new NamespaceListing { Namespace = "balenthiran", Error = "connect ETIMEDOUT" }], NoPg());

        var plan = new Planner().Plan([declaration], obs);
        Assert.Empty(plan.Steps);
        Assert.Equal("Jwt__Secret", Assert.Single(plan.Unknown).Key);
    }
}
