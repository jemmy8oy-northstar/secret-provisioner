using System.Text.Json;
using System.Text.Json.Nodes;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// The REAL observer, planner and executor, over a fake estate that remembers what was created.
/// Every other test here proves one slice; these prove the slices were joined the right way round
/// [[both-layers-right-assembly-broken]] — above all that a second reconcile sees the first one's
/// work, which is the property that makes an on-demand, create-only service safe to press twice.
/// </summary>
public class ReconcilerTests
{
    private static readonly PostgresEndpoint Endpoint = new() { Host = "pg-postgresql.data.svc.cluster.local" };

    [Fact]
    public async Task Plan_is_exactly_what_the_planner_makes_of_the_observation_the_status_shows()
    {
        var estate = new Estate();
        var reconciler = Reconciler(estate);

        var status = await reconciler.StatusAsync();
        var plan = await reconciler.PlanAsync();

        var direct = new Planner().Plan(Declarations(), status.Observation);
        Assert.Equal(Serialise(direct), Serialise(plan.Plan!));
        Assert.True(plan.Plan!.Summary.Steps > 0, "an empty estate must plan something, or this test compares two empty plans");
        Assert.Empty(estate.Writes);
    }

    [Fact]
    public async Task Status_carries_the_commit_and_the_observation_and_nothing_else()
    {
        var json = JsonNode.Parse(Serialise(await Reconciler(new Estate()).StatusAsync()))!.AsObject();

        Assert.Equal(["source", "observation"], json.Select(p => p.Key));
        Assert.Equal(Estate.Sha, (string?)json["source"]!["sha"]);
    }

    [Fact]
    public async Task Only_the_declared_namespaces_are_listed_and_each_only_once()
    {
        var estate = new Estate();
        await Reconciler(estate).PlanAsync();

        var declared = Declarations().Select(d => d.Namespace).Distinct().Order().ToList();
        Assert.Equal(declared, estate.Listed.Order().ToList());
    }

    [Fact]
    public async Task A_reconcile_writes_to_the_estate_and_its_response_carries_no_value_it_generated()
    {
        var estate = new Estate();
        var view = await Reconciler(estate).ReconcileAsync();

        Assert.Null(view.Report!.Failed);
        Assert.NotEmpty(estate.Values);
        var wire = Serialise(view);
        foreach (var value in estate.Values)
        {
            Assert.DoesNotContain(value, wire);
        }
    }

    [Fact]
    public async Task A_second_reconcile_sees_the_first_ones_work_and_creates_nothing()
    {
        var estate = new Estate();
        var reconciler = Reconciler(estate);

        var first = await reconciler.ReconcileAsync();
        var written = estate.Writes.Count;
        var second = await reconciler.ReconcileAsync();

        Assert.True(first.Report!.Summary.Done > 0);

        // Postgres was really looked at: a database key that went "unknown" would also plan no
        // step the second time, so zero steps alone cannot tell "done" from "never inspected".
        Assert.Contains(estate.Writes, w => w.StartsWith("role ", StringComparison.Ordinal));
        Assert.Empty(second.Plan!.Unknown);
        Assert.Equal(0, second.Plan!.Summary.Steps);
        Assert.Equal(written, estate.Writes.Count);
    }

    [Fact]
    public async Task A_reconcile_reads_the_declarations_once_and_executes_against_that_read()
    {
        // A second read could land on a newer commit than the plan was made from, and the
        // executor resolves every step's parameters from the declarations it is handed.
        var estate = new Estate();
        await Reconciler(estate).ReconcileAsync();

        Assert.Equal(1, estate.Loads);
    }

    [Fact]
    public async Task A_Postgres_that_cannot_be_read_fails_the_call_and_writes_nothing()
    {
        var estate = new Estate { PostgresDown = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Reconciler(estate).ReconcileAsync());
        Assert.Empty(estate.Writes);
    }

    private static Reconciler Reconciler(Estate estate) =>
        new(estate, estate, estate, new Observer(), new Planner(), new Executor(estate, estate), Endpoint);

    private static List<SecretDeclaration> Declarations() =>
        JsonNode.Parse(File.ReadAllText(Json.Fixture("Execute", "declarations.json")))!.Deserialize<List<SecretDeclaration>>(Json.Options)!;

    private static string Serialise<T>(T value) => JsonSerializer.Serialize(value, Json.Options);

    /// <summary>
    /// A cluster and a Postgres that remember. Records the generated VALUES as well as the writes,
    /// so a test can assert they reached the estate and not the response.
    /// </summary>
    private sealed class Estate : IDeclarationSource, IKubernetesClient, IPostgresClient
    {
        public const string Sha = "0123456789abcdef0123456789abcdef01234567";

        private readonly Dictionary<string, Dictionary<string, string?>> _secrets = new(StringComparer.Ordinal);
        private readonly List<string?> _roles = ["postgres"];
        private readonly List<string?> _databases = ["postgres"];

        public bool PostgresDown { get; init; }

        public int Loads { get; private set; }

        public List<string> Listed { get; } = [];

        public List<string> Writes { get; } = [];

        public List<string> Values { get; } = [];

        public Task<IDeclarationSet> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult<IDeclarationSet>(new DeclarationSet { Owner = "jemmy8oy-northstar", Repo = "oke-fleet", Branch = "main", Sha = Sha, Declarations = Declarations() });
        }

        public Task<IReadOnlyList<INamespaceListing>> ObserveNamespacesAsync(IEnumerable<string> namespaces, CancellationToken cancellationToken = default)
        {
            var listings = namespaces.Select(ns =>
            {
                Listed.Add(ns);
                var items = _secrets.Where(s => s.Key.StartsWith(ns + "/", StringComparison.Ordinal))
                    .Select(s => new KubeSecret { Metadata = new KubeObjectMeta { Name = s.Key[(ns.Length + 1)..], Namespace = ns }, Data = s.Value })
                    .ToList();
                return (INamespaceListing)new NamespaceListing { Namespace = ns, Listing = new KubeSecretList { Items = items } };
            }).ToList();
            return Task.FromResult<IReadOnlyList<INamespaceListing>>(listings);
        }

        public Task<IPostgresCatalogue> ReadCatalogueAsync(CancellationToken cancellationToken = default) =>
            PostgresDown
                ? throw new InvalidOperationException("connection refused")
                : Task.FromResult<IPostgresCatalogue>(new PostgresCatalogue { RoleNames = [.. _roles], DatabaseNames = [.. _databases] });

        public Task CreateRoleAsync(string role, string password, CancellationToken cancellationToken = default)
        {
            _roles.Add(role);
            Values.Add(password);
            Writes.Add("role " + role);
            return Task.CompletedTask;
        }

        public Task CreateDatabaseAsync(string database, string owner, CancellationToken cancellationToken = default)
        {
            _databases.Add(database);
            Writes.Add("database " + database);
            return Task.CompletedTask;
        }

        public Task CreateSecretAsync(string ns, string name, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default)
        {
            _secrets.Add(ns + "/" + name, data.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));
            Values.AddRange(data.Values);
            Writes.Add("secret " + ns + "/" + name);
            return Task.CompletedTask;
        }

        public Task PatchSecretKeyAsync(string ns, string name, string key, string value, CancellationToken cancellationToken = default)
        {
            _secrets[ns + "/" + name].Add(key, value);
            Values.Add(value);
            Writes.Add("patch " + ns + "/" + name + "#" + key);
            return Task.CompletedTask;
        }

        public Task<IKubeSecretList> ListSecretsAsync(string ns, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
