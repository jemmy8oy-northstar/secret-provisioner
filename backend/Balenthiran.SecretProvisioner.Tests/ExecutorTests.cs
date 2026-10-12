using System.Text.Json;
using System.Text.Json.Nodes;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// THE PLAN FIXTURES ARE THE REAL PLANNER'S OUTPUT, NOT A DRAWING OF IT. <c>Fixtures/Execute</c>
/// holds what <c>oke-fleet:scripts/plan-secrets.mjs</c> produced over oke-fleet's actual
/// <c>secrets/*.json</c>, and the declarations its validator returned in the same run. This
/// module is one side of a seam across two repositories: a hand-written fixture would test my
/// memory of the plan shape, which is the thing most likely to be wrong.
/// </summary>
public class ExecutorTests
{
    private static readonly PostgresEndpoint Endpoint = new() { Host = "pg-postgresql.data.svc.cluster.local" };

    [Fact]
    public void GenerateHex_produces_only_hex_of_the_length_asked_for()
    {
        var value = Executor.GenerateHex(32);
        Assert.Equal(64, value.Length);
        Assert.Matches("^[0-9a-f]+$", value);
        Assert.NotEqual(Executor.GenerateHex(32), Executor.GenerateHex(32));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GenerateHex_refuses_a_byte_count_that_is_not_a_positive_integer(int bytes)
    {
        var refusal = Assert.Throws<ExecutionRefusedException>(() => Executor.GenerateHex(bytes));
        Assert.Contains("positive integer", refusal.Message);
    }

    [Fact]
    public void The_connection_string_is_Npgsql_keyword_syntax_which_is_what_the_apps_consume()
    {
        // Not a postgres:// URL. DATABASE_URL is a key NAME: balenthiran.co.uk binds that key to
        // the env var ConnectionStrings__DefaultConnection, and both apps use Npgsql.
        Assert.Equal(
            "Host=pg;Port=5432;Database=app;Username=app;Password=abc123",
            Executor.NpgsqlConnectionString("pg", 5432, "app", "app", "abc123"));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("database")]
    [InlineData("username")]
    [InlineData("password")]
    public void The_connection_string_refuses_a_component_it_would_have_to_quote(string field)
    {
        foreach (var bad in new[] { "a;b", "a'b", "a\"b", "a=b" })
        {
            var refusal = Assert.Throws<ExecutionRefusedException>(() => Executor.NpgsqlConnectionString(
                field == "host" ? bad : "pg",
                5432,
                field == "database" ? bad : "app",
                field == "username" ? bad : "app",
                field == "password" ? bad : "abc123"));
            Assert.Contains("does not quote", refusal.Message);
            Assert.DoesNotContain(bad, refusal.Message);
        }
    }

    [Fact]
    public void The_connection_string_refuses_a_missing_component_rather_than_writing_nothing()
    {
        Assert.Contains("Host is required", Assert.Throws<ExecutionRefusedException>(() => Executor.NpgsqlConnectionString(null, 5432, "a", "b", "c")).Message);
        Assert.Contains("Password is required", Assert.Throws<ExecutionRefusedException>(() => Executor.NpgsqlConnectionString("p", 5432, "a", "b", null)).Message);
        Assert.Contains("Password is required", Assert.Throws<ExecutionRefusedException>(() => Executor.NpgsqlConnectionString("p", 5432, "a", "b", "")).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void The_connection_string_refuses_a_port_that_is_not_a_port(int port)
    {
        var refusal = Assert.Throws<ExecutionRefusedException>(() => Executor.NpgsqlConnectionString("pg", port, "app", "app", "abc123"));
        Assert.Contains("Port must be an integer", refusal.Message);
        Assert.Contains(";Port=65535;", Executor.NpgsqlConnectionString("pg", 65535, "app", "app", "abc123"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public async Task Execute_refuses_a_bad_port_rather_than_creating_roles_and_then_writing_it(int port)
    {
        var recorder = new Recorder();
        var refusal = await Assert.ThrowsAsync<InvalidPostgresEndpointException>(() =>
            new Executor(recorder, recorder).ExecuteAsync(Plan("empty-estate.plan.json").Deserialize<Plan>(Json.Options)!, Declarations(), new PostgresEndpoint { Host = "pg", Port = port }));
        Assert.Contains("config.postgres.port must be an integer", refusal.Message);

        // Before anything was created, not after.
        Assert.Empty(recorder.Roles);
    }

    [Fact]
    public void A_hex_password_never_needs_quoting_which_is_why_values_are_hex_and_not_base64()
    {
        // base64 emits `+`, `/` and `=`, and `=` inside a keyword-value string is where the
        // string stops meaning what it looks like it means. 200 samples catches an encoding change.
        for (var i = 0; i < 200; i++)
        {
            Executor.NpgsqlConnectionString("pg", 5432, "d", "u", Executor.GenerateHex(16));
        }
    }

    [Fact]
    public async Task An_empty_estate_runs_every_step_in_the_planners_order()
    {
        var recorder = new Recorder();
        var report = await Execute(recorder, Plan("empty-estate.plan.json"));

        Assert.Null(report.Failed);
        Assert.Equal(6, report.Summary.Done);
        Assert.Equal(0, report.Summary.Skipped);
        Assert.Equal(["create-role", "create-database", "create-role", "create-database", "create-secret", "create-secret"], report.Done.Select(d => d.Kind));

        // The DDL ran before the writes. A connection string cannot be composed before the role it
        // names exists, and that ordering is the planner's.
        Assert.Equal(["aroundtheworld", "balenthiran"], recorder.Roles.Select(c => c.Role));
        Assert.Equal([("aroundtheworld", "aroundtheworld"), ("balenthiran", "balenthiran")], recorder.Databases);
        Assert.Equal(["role", "database", "role", "database", "secret", "secret"], recorder.Order);
    }

    [Fact]
    public async Task The_password_put_in_the_connection_string_is_the_one_the_role_was_created_with()
    {
        // The join this class exists to make. The plan step that writes #DATABASE_URL carries no
        // role, no database and no password; get the join wrong and the Secret holds a credential
        // that authenticates against nothing, and create-only means it can never be rewritten.
        var recorder = new Recorder();
        await Execute(recorder, Plan("empty-estate.plan.json"));

        var passwords = recorder.Roles.ToDictionary(c => c.Role, c => c.Password);
        var atw = recorder.Secrets.Single(c => c.Name == "around-the-world-secrets");
        var bal = recorder.Secrets.Single(c => c.Name == "balenthiran-secrets");

        Assert.Equal(
            $"Host={Endpoint.Host};Port={Executor.DefaultPostgresPort};Database=aroundtheworld;Username=aroundtheworld;Password={passwords["aroundtheworld"]}",
            atw.Data["ConnectionStrings__DefaultConnection"]);
        Assert.Equal(
            $"Host={Endpoint.Host};Port={Executor.DefaultPostgresPort};Database=balenthiran;Username=balenthiran;Password={passwords["balenthiran"]}",
            bal.Data["DATABASE_URL"]);

        // ...and not each other's.
        Assert.NotEqual(passwords["aroundtheworld"], passwords["balenthiran"]);
    }

    [Fact]
    public async Task An_explicit_port_is_the_one_composed()
    {
        var recorder = new Recorder();
        await Execute(recorder, Plan("empty-estate.plan.json"), endpoint: new PostgresEndpoint { Host = "pg", Port = 6543 });

        Assert.Contains("Host=pg;Port=6543;", recorder.Secrets[0].Data["ConnectionStrings__DefaultConnection"]);
    }

    [Fact]
    public async Task The_role_goes_in_Username_and_the_database_in_Database_they_are_not_the_same_field()
    {
        // Both live declarations name role == database, so NO fixture drawn from the real estate
        // can tell the two apart: swap them and every assertion above still passes. This one is
        // synthetic for exactly that reason.
        var golden = JsonNode.Parse(File.ReadAllText(Json.Fixture("Execute", "distinct-role-and-database.json")))!;
        var recorder = new Recorder();
        var report = await new Executor(recorder, recorder).ExecuteAsync(
            golden["plan"].Deserialize<Plan>(Json.Options)!,
            golden["declarations"].Deserialize<List<SecretDeclaration>>(Json.Options)!,
            Endpoint);

        Assert.Null(report.Failed);
        Assert.Equal("distinct_role", recorder.Roles[0].Role);
        Assert.Equal(("distinct_db", "distinct_role"), recorder.Databases[0]);
        Assert.Matches(";Database=distinct_db;Username=distinct_role;Password=[0-9a-f]{64}$", recorder.Secrets[0].Data["ConnectionStrings__DefaultConnection"]);
    }

    [Fact]
    public async Task The_byte_count_comes_from_the_declaration_not_from_the_steps_prose()
    {
        // The step says "generate 16 random bytes, hex-encoded" in an English sentence. Admin__Key
        // is declared as 16 bytes and Jwt__Secret as 32; if this class were defaulting both they
        // would be equal length and the difference here is the only thing that would notice.
        var recorder = new Recorder();
        await Execute(recorder, Plan("empty-estate.plan.json"));

        var atw = recorder.Secrets.Single(c => c.Name == "around-the-world-secrets");
        Assert.Equal(64, atw.Data["Jwt__Secret"].Length);
        Assert.Equal(32, atw.Data["Admin__Key"].Length);
    }

    [Fact]
    public async Task No_generated_value_reaches_the_report_in_any_shape()
    {
        // The report is what POST /reconcile serialises to an HTTP response, and the difference
        // between reporting a key name and reporting its value is one property access.
        var recorder = new Recorder();
        var report = await Execute(recorder, Plan("empty-estate.plan.json"));
        var serialised = JsonSerializer.Serialize((ExecutionReport)report, Json.Options);

        var values = recorder.Roles.Select(c => c.Password).Concat(recorder.Secrets.SelectMany(c => c.Data.Values)).ToList();
        Assert.Equal(6, values.Count);
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, serialised);
        }

        // Every one of those six is REGISTERED, not merely absent. RedactValues has a shape pass
        // that would scrub a hex password out of an error message whether or not it was tracked,
        // so absence from the report is no evidence AssertNoGeneratedValues covers it. This count
        // is what makes the registration observable.
        Assert.Equal(6, report.Summary.Generated);

        // ...and the report did carry the identities, so the assertions above are not passing by emptiness.
        Assert.Contains("around-the-world-secrets", serialised);
        Assert.Contains("Jwt__Secret", serialised);
        Assert.Contains("aroundtheworld", serialised);
    }

    [Fact]
    public async Task A_create_secret_step_reports_key_NAMES_and_the_report_has_no_data_field_at_all()
    {
        var report = await Execute(new Recorder(), Plan("empty-estate.plan.json"));
        var created = report.Done.Single(d => d.Kind == "create-secret" && d.SecretName == "around-the-world-secrets");

        Assert.Equal(["ConnectionStrings__DefaultConnection", "Jwt__Secret", "Admin__Key"], created.Keys);
        var node = JsonSerializer.SerializeToNode((ExecutionReport)report, Json.Options)!;
        Assert.All(node["done"]!.AsArray(), step => Assert.Null(step!["data"]));
    }

    [Fact]
    public async Task A_partial_estate_never_writes_into_the_Secret_that_already_exists()
    {
        // around-the-world-secrets already holds five keys and is missing Jwt__Secret. Create-only
        // (secret-provisioner#9) leaves it to a human: the plan carries it as blocked, and the run
        // writes only the Secret that did not exist.
        var recorder = new Recorder();
        var report = await Execute(recorder, Plan("partial-estate.plan.json"));

        Assert.Null(report.Failed);
        Assert.Equal(["balenthiran-secrets"], recorder.Secrets.Select(c => c.Name));
        var blocked = Assert.Single(report.Blocked);
        Assert.Equal(("around-the-world-secrets", "Jwt__Secret", "add-by-hand"), (blocked.SecretName, blocked.Key, blocked.Action));
    }

    [Fact]
    public async Task A_patch_step_is_refused_not_executed()
    {
        // The executor has no verb to carry one out, and the cluster would refuse it anyway. A plan
        // that still says "patch" came from an older planner, and must stop the run.
        var plan = Plan("empty-estate.plan.json");
        plan["steps"]!.AsArray().Insert(0, JsonNode.Parse("""{"kind":"patch-secret-key","namespace":"balenthiran","secretName":"around-the-world-secrets","key":"Jwt__Secret","type":"random","detail":"patch it"}"""));
        var recorder = new Recorder();
        var report = await Execute(recorder, plan);

        Assert.Contains("no executor branch for step kind \"patch-secret-key\"", report.Failed!.Error);
        Assert.Empty(recorder.Secrets);
        Assert.Empty(recorder.Roles);
    }

    [Fact]
    public async Task The_first_failure_stops_the_run_and_the_rest_is_reported_as_not_attempted()
    {
        var recorder = new Recorder { OnCreateDatabase = () => throw new InvalidOperationException("pq: permission denied to create database") };
        var report = await Execute(recorder, Plan("empty-estate.plan.json"));

        Assert.Equal(1, report.Summary.Done);
        Assert.Equal("create-database", report.Failed!.Step.Kind);
        Assert.Contains("permission denied", report.Failed.Error);
        Assert.Equal(4, report.Summary.Skipped);
        Assert.Equal(["create-role", "create-database", "create-secret", "create-secret"], report.Skipped.Select(s => s.Kind));

        // Not attempted means NOT ATTEMPTED — no Secret was written after the failure.
        Assert.Empty(recorder.Secrets);
        Assert.Single(recorder.Roles);
    }

    [Fact]
    public async Task A_password_in_an_error_message_is_redacted_before_it_is_recorded()
    {
        // Postgres quotes the failing statement back at you, and for CREATE ROLE the statement
        // contains the password. Without this the report — a public HTTP response — carries the
        // credential that was just created.
        string? leaked = null;
        var recorder = new Recorder
        {
            OnCreateRole = password =>
            {
                leaked = password;
                throw new InvalidOperationException($"pq: syntax error at or near \"CREATE ROLE aroundtheworld LOGIN PASSWORD '{password}'\"");
            },
        };
        var report = await Execute(recorder, Plan("empty-estate.plan.json"));

        Assert.NotNull(leaked);
        Assert.DoesNotContain(leaked, JsonSerializer.Serialize((ExecutionReport)report, Json.Options));
        Assert.Contains("<redacted>", report.Failed!.Error);
        Assert.Contains("syntax error", report.Failed.Error);
    }

    [Fact]
    public async Task The_password_is_registered_as_generated_before_the_role_is_created()
    {
        // The shape pass would scrub the real (hex) password even if it were never registered, so
        // the test above cannot tell. Registering BEFORE CreateRoleAsync is observable only in the
        // count: the role call threw, so the only generated value is that password.
        var recorder = new Recorder { OnCreateRole = _ => throw new InvalidOperationException("boom") };
        var report = await Execute(recorder, Plan("empty-estate.plan.json"));

        Assert.Equal(1, report.Summary.Generated);
    }

    [Fact]
    public void RedactValues_replaces_the_longest_value_first()
    {
        // A value that contains another must not be half-replaced into something that no longer
        // matches and then survives.
        Assert.Equal("x <redacted> y <redacted> z", Executor.RedactValues("x abcdef y abcd z", ["abcd", "abcdef"]));
    }

    [Fact]
    public void RedactValues_also_catches_a_value_the_driver_truncated()
    {
        // Exact matching has one blind spot and it is the realistic one: a message that quotes only
        // part of a value no longer equals anything we hold. The schema's floor is 16 bytes, so 32
        // hex characters is the shortest thing we can make.
        var full = Executor.GenerateHex(32);
        var truncated = full[..48];
        var output = Executor.RedactValues($"pq: error near \"{truncated}…\"", [full]);

        Assert.DoesNotContain(truncated, output);
        Assert.Contains("<redacted>", output);
        Assert.Contains("pq: error near", output);
    }

    [Fact]
    public void RedactValues_leaves_ordinary_prose_alone()
    {
        // The shape pass must not eat the diagnostic it is protecting.
        const string text = "pq: role \"aroundtheworld\" already exists — balenthiran/around-the-world-secrets#Jwt__Secret deadbeef";
        Assert.Equal(text, Executor.RedactValues(text, []));
    }

    [Fact]
    public void AssertNoGeneratedValues_throws_on_a_report_that_leaks_called_directly()
    {
        // Unreachable through ExecuteAsync by construction, and a guard no test can execute is
        // indistinguishable from one that does not work.
        string[] values = ["deadbeef"];
        Executor.AssertNoGeneratedValues(ReportWith(note: "wrote the key"), values);
        Assert.Contains("reached the report", Assert.Throws<InvalidOperationException>(() => Executor.AssertNoGeneratedValues(ReportWith(note: "wrote deadbeef"), values)).Message);

        // Anywhere in the report, not just in the fields anyone thought of.
        Assert.Throws<InvalidOperationException>(() => Executor.AssertNoGeneratedValues(ReportWith(note: "xxdeadbeefxx"), values));
    }

    [Fact]
    public async Task Refuses_to_run_without_a_Postgres_host_rather_than_guessing_one()
    {
        var recorder = new Recorder();
        var executor = new Executor(recorder, recorder);
        var plan = Plan("empty-estate.plan.json").Deserialize<Plan>(Json.Options)!;

        var refusal = await Assert.ThrowsAsync<InvalidPostgresEndpointException>(() => executor.ExecuteAsync(plan, Declarations(), new PostgresEndpoint { Host = "" }));
        Assert.Contains("postgres.host is required", refusal.Message);
        Assert.Empty(recorder.Roles);
    }

    [Fact]
    public async Task Refuses_a_plan_that_writes_a_key_no_declaration_claims()
    {
        var plan = Plan("empty-estate.plan.json");
        plan["steps"]!.AsArray().Last()!["keys"]![0]!["key"] = "DATABASE_URL_TYPO";
        var report = await Execute(new Recorder(), plan);

        Assert.Contains("which no declaration claims", report.Failed!.Error);
    }

    [Fact]
    public async Task Refuses_a_plan_whose_type_disagrees_with_the_declaration()
    {
        // The two are joined on identity alone, so the type is the only thing that can say
        // "these came from different reads".
        var decls = Declarations(d => d[0]!["keys"]![1]!["type"] = "external");
        var report = await Execute(new Recorder(), Plan("empty-estate.plan.json"), decls);

        Assert.Matches("the plan writes .*Jwt__Secret as random, the declaration says external", report.Failed!.Error);
    }

    [Fact]
    public async Task Refuses_to_write_a_connection_string_for_a_role_this_run_did_not_create()
    {
        // The planner never emits this — a postgres-db key whose role already exists is reported
        // as blocked. So it is a plan contradicting itself, and inventing a password here would
        // produce a credential that authenticates against nothing.
        var plan = Plan("empty-estate.plan.json");
        var steps = plan["steps"]!.AsArray();
        foreach (var step in steps.Where(s => (string)s!["kind"]! == "create-role").ToList())
        {
            steps.Remove(step);
        }

        var report = await Execute(new Recorder(), plan);

        Assert.Contains("does not create role aroundtheworld in the same run", report.Failed!.Error);
    }

    [Fact]
    public async Task Refuses_a_plan_that_creates_the_same_role_twice()
    {
        // Left alone, the second password overwrites the first in the map while the FIRST is the
        // one the server was given — so the connection string would be composed from a password
        // that was never set.
        var plan = Plan("empty-estate.plan.json");
        var steps = plan["steps"]!.AsArray();
        steps.Insert(1, steps[0]!.DeepClone());
        var recorder = new Recorder();
        var report = await Execute(recorder, plan);

        Assert.Contains("creates role aroundtheworld twice", report.Failed!.Error);
        Assert.Single(recorder.Roles);
    }

    [Fact]
    public async Task Refuses_a_step_kind_it_does_not_know()
    {
        // The planner and this executor live in different repositories. A fifth kind added there
        // must stop the run, not fall through it as a silent success.
        var plan = Plan("empty-estate.plan.json");
        plan["steps"] = new JsonArray(JsonNode.Parse("""{"kind":"delete-secret","namespace":"balenthiran","secretName":"around-the-world-secrets","detail":"nope"}"""));
        var recorder = new Recorder();
        var report = await Execute(recorder, plan);

        Assert.Contains("no executor branch for step kind \"delete-secret\"", report.Failed!.Error);
        Assert.Empty(recorder.Secrets);
    }

    [Fact]
    public async Task Refuses_to_generate_an_external_value_even_if_a_plan_asks_for_one()
    {
        var plan = Plan("empty-estate.plan.json");
        plan["steps"]!.AsArray().Last()!["keys"] = JsonNode.Parse("""[{"key":"DATABASE_URL","type":"external","source":"nothing can make this"}]""");
        var decls = Declarations(d => d[1]!["keys"]![0] = JsonNode.Parse("""{"key":"DATABASE_URL","type":"external"}"""));
        var recorder = new Recorder();
        var report = await Execute(recorder, plan, decls);

        Assert.Contains("type \"external\", which nothing here can generate", report.Failed!.Error);
        Assert.DoesNotContain(recorder.Secrets, s => s.Name == "balenthiran-secrets");
    }

    [Fact]
    public async Task The_planners_refusals_are_carried_through_verbatim_and_do_not_stop_anything()
    {
        // blocked/unknown/drift/unmanaged are decisions the planner already made. Treating them as
        // failures would mean one un-generatable API key blocks every other app's provisioning.
        var planNode = Plan("empty-estate.plan.json");
        var plan = planNode.Deserialize<Plan>(Json.Options)!;
        var report = await new Executor(new Recorder(), new Recorder()).ExecuteAsync(plan, Declarations(), Endpoint);

        Assert.Equal(plan.Blocked, report.Blocked);
        Assert.Equal(3, report.Summary.Blocked);
        Assert.Null(report.Failed);
        Assert.Equal(6, report.Summary.Done);
    }

    [Fact]
    public async Task Noop_is_carried_through_too_a_settled_estate_is_mostly_noop()
    {
        // noop and unmanaged are the same kind of thing: an existing key nothing touched. Dropping
        // one makes a run that correctly did nothing look like a run that saw nothing.
        var plan = Plan("partial-estate.plan.json").Deserialize<Plan>(Json.Options)!;
        Assert.Equal(5, plan.Noop.Count);

        var recorder = new Recorder();
        var report = await new Executor(recorder, recorder).ExecuteAsync(plan, Declarations(), Endpoint);

        Assert.Equal(plan.Noop, report.Noop);
        Assert.Equal(5, report.Summary.Noop);
        Assert.Equal(1, report.Summary.Unmanaged);
    }

    [Fact]
    public void IndexDeclarations_keys_on_exactly_the_identity_a_plan_step_carries()
    {
        var index = Executor.IndexDeclarations(Declarations());
        Assert.Equal(16, index["balenthiran/around-the-world-secrets#Admin__Key"].Bytes);
        Assert.Equal("balenthiran", index["balenthiran/balenthiran-secrets#DATABASE_URL"].Role);
        Assert.False(index.ContainsKey("balenthiran/nope#nope"));
        Assert.Empty(Executor.IndexDeclarations(null));
    }

    [Fact]
    public async Task An_empty_plan_is_a_clean_run_not_an_error()
    {
        var plan = Plan("empty-estate.plan.json");
        foreach (var bucket in new[] { "steps", "blocked" })
        {
            plan[bucket] = new JsonArray();
        }

        var report = await Execute(new Recorder(), plan);

        Assert.Equal(0, report.Summary.Done + report.Summary.Failed + report.Summary.Skipped + report.Summary.Generated + report.Summary.Blocked
            + report.Summary.Unknown + report.Summary.Drift + report.Summary.Unmanaged + report.Summary.Noop);
    }

    private static Task<IExecutionReport> Execute(Recorder recorder, JsonNode plan, List<SecretDeclaration>? declarations = null, IPostgresEndpoint? endpoint = null) =>
        new Executor(recorder, recorder).ExecuteAsync(plan.Deserialize<Plan>(Json.Options)!, declarations ?? Declarations(), endpoint ?? Endpoint);

    private static JsonNode Plan(string name) => JsonNode.Parse(File.ReadAllText(Json.Fixture("Execute", name)))!;

    private static List<SecretDeclaration> Declarations(Action<JsonNode>? edit = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(Json.Fixture("Execute", "declarations.json")))!;
        edit?.Invoke(node);
        return node.Deserialize<List<SecretDeclaration>>(Json.Options)!;
    }

    private static ExecutionReport ReportWith(string note) => new()
    {
        Done = [new ExecutedStep { Kind = "create-secret", Detail = note }],
        Skipped = [],
        Blocked = [],
        Unknown = [],
        Drift = [],
        Unmanaged = [],
        Noop = [],
        Summary = new ExecutionSummary { Done = 1, Failed = 0, Skipped = 0, Generated = 0, Blocked = 0, Unknown = 0, Drift = 0, Unmanaged = 0, Noop = 0 },
    };

    /// <summary>
    /// Records everything it was asked to do, INCLUDING the values — which is the point: the test
    /// can then assert the values went to the cluster and NOT to the report. A fake that discarded
    /// them could not tell those two apart.
    /// </summary>
    private sealed class Recorder : IKubernetesClient, IPostgresClient
    {
        public List<(string Role, string Password)> Roles { get; } = [];

        public List<(string Database, string Owner)> Databases { get; } = [];

        public List<(string Namespace, string Name, IReadOnlyDictionary<string, string> Data)> Secrets { get; } = [];

        public List<string> Order { get; } = [];

        public Action? OnCreateDatabase { get; init; }

        public Action<string>? OnCreateRole { get; init; }

        public Task CreateRoleAsync(string role, string password, CancellationToken cancellationToken = default)
        {
            Roles.Add((role, password));
            Order.Add("role");
            OnCreateRole?.Invoke(password);
            return Task.CompletedTask;
        }

        public Task CreateDatabaseAsync(string database, string owner, CancellationToken cancellationToken = default)
        {
            Databases.Add((database, owner));
            Order.Add("database");
            OnCreateDatabase?.Invoke();
            return Task.CompletedTask;
        }

        public Task CreateSecretAsync(string ns, string name, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default)
        {
            Secrets.Add((ns, name, data));
            Order.Add("secret");
            return Task.CompletedTask;
        }

        public Task<IPostgresCatalogue> ReadCatalogueAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ISecretProbe>> ProbeSecretsAsync(IEnumerable<(string Namespace, string Name)> secrets, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
