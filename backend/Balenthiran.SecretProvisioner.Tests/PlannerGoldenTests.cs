using System.Text.Json;
using System.Text.Json.Nodes;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// The C# planner against the JavaScript planner's OWN output. Every golden in
/// <c>Fixtures/Plan</c> was written by <c>tools/plan-goldens.mjs</c> running
/// <c>oke-fleet:scripts/plan-secrets.mjs</c> at 34c7d8c, so the expected side never went
/// through this code — a comparison that canonicalised both sides through one serialiser would
/// be blind to the serialiser.
/// </summary>
public class PlannerGoldenTests
{
    public static TheoryData<string> Plans() => Golden(refusals: false);

    public static TheoryData<string> Refusals() => Golden(refusals: true);

    [Theory]
    [MemberData(nameof(Plans))]
    public void Plans_exactly_what_the_javascript_planner_planned(string name)
    {
        var golden = Load(name);
        var plan = new Planner().Plan(Declarations(golden), Observation(golden));

        var actual = JsonSerializer.SerializeToNode((Plan)plan, Json.Options);
        var expected = golden["plan"];
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{name}: the C# plan differs from the JS planner's.\nexpected: {expected}\nactual:   {actual}");
    }

    [Fact]
    public void The_goldens_cover_every_bucket_and_every_step_kind()
    {
        // A golden set that never exercised a bucket would pass over a broken branch. Count it.
        var plans = Names(Plans()).Select(name => Load(name)["plan"]!).ToList();
        foreach (var bucket in new[] { "blocked", "unknown", "drift", "unmanaged", "noop" })
        {
            Assert.Contains(plans, p => p[bucket]!.AsArray().Count > 0);
        }

        var kinds = plans.SelectMany(p => p["steps"]!.AsArray()).Select(s => (string)s!["kind"]!).ToHashSet();
        Assert.Equal(["create-database", "create-role", "create-secret", "patch-secret-key"], kinds.Order());
        Assert.True(plans.Count >= 15, $"only {plans.Count} plan goldens — did the fixtures copy to the output directory?");
    }

    // The JS planner took raw JSON, so it refused malformed shapes itself. Typed C# moves two of
    // those refusals to the deserialiser; the other two must still be the planner's, word for word.
    [Theory]
    [InlineData("refuse-no-namespaces", true)]
    [InlineData("refuse-bad-postgres", true)]
    [InlineData("refuse-not-an-object", false)]
    [InlineData("refuse-secret-keys-not-array", false)]
    public void Refuses_an_observation_that_does_not_say_what_it_looked_at(string name, bool plannerRefuses)
    {
        var golden = Load(name);
        if (!plannerRefuses)
        {
            Assert.Throws<JsonException>(() => Observation(golden));
            return;
        }

        var refusal = Assert.Throws<InvalidObservationException>(() => new Planner().Plan(Declarations(golden), Observation(golden)));
        Assert.Equal((string)golden["error"]!, refusal.Message);
    }

    [Fact]
    public void Every_refusal_golden_has_a_case_above()
    {
        var covered = typeof(PlannerGoldenTests).GetMethod(nameof(Refuses_an_observation_that_does_not_say_what_it_looked_at))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Select(a => (string)((InlineDataAttribute)a).GetData(null!).First()[0])
            .Order();
        Assert.Equal(Names(Refusals()).Order(), covered);
    }

    private static IEnumerable<string> Names(TheoryData<string> data) => ((IEnumerable<object[]>)data).Select(row => (string)row[0]);

    [Fact]
    public void A_null_secret_key_list_is_refused_in_the_planners_words()
    {
        var observation = new Observation { Namespaces = ["balenthiran"], Secrets = new() { ["balenthiran/x"] = null } };
        var refusal = Assert.Throws<InvalidObservationException>(() => new Planner().Plan([], observation));
        Assert.Equal("observation: secrets[\"balenthiran/x\"] must be an array of key names", refusal.Message);
    }

    private static TheoryData<string> Golden(bool refusals)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Json.Fixture("Plan"), "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("refuse-", StringComparison.Ordinal) == refusals)
            {
                data.Add(name);
            }
        }

        return data;
    }

    private static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Json.Fixture("Plan", $"{name}.json")))!;

    private static List<SecretDeclaration> Declarations(JsonNode golden) =>
        golden["declarations"].Deserialize<List<SecretDeclaration>>(Json.Options)!;

    private static Observation Observation(JsonNode golden) =>
        golden["observation"].Deserialize<Observation>(Json.Options)!;
}
