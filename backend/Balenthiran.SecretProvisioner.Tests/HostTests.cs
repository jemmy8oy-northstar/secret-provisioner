using System.Net;
using System.Text;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.Services;
using Balenthiran.SecretProvisioner.WebApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// The router's tests drive <c>RespondAsync</c> directly. These drive a real Kestrel over a real
/// socket, because the plumbing — the singleton that holds the lock, the status line, the Allow
/// header, the log — is where a rule proven at the layer below can still fail to be wired up
/// [[both-layers-right-assembly-broken]]. Ported from the two socket tests in router.test.js.
/// </summary>
public class HostTests
{
    private const string Connection = "Host=pg-postgresql.data.svc.cluster.local;Port=5433;Database=postgres;Username=provisioner;Password=hunter2hunter2";

    [Fact]
    public async Task End_to_end_a_real_server_a_real_client_and_every_rule_still_holds()
    {
        var leak = new string('a', 64);
        var handlers = new Handlers { Plan = () => throw new InvalidOperationException($"syntax error at PASSWORD '{leak}'") };
        var logs = new CapturedLogs();
        await using var app = await Start(handlers, logs);
        using var http = Client(app);

        var status = await http.GetAsync("/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("application/json", status.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"ok\": \"status\"", await status.Content.ReadAsStringAsync());

        var get = await http.GetAsync("/reconcile");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal("POST", string.Join(", ", get.Content.Headers.Allow));

        var empty = await http.PostAsync("/reconcile", null);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);

        var withBody = await http.PostAsync("/reconcile", new StringContent("x", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, withBody.StatusCode);
        Assert.Equal(1, handlers.Reconciles);

        var failed = await http.GetAsync("/plan");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain(leak, await failed.Content.ReadAsStringAsync());

        // Refusing to echo is not swallowing: the failure reaches the log, with the value masked.
        var line = Assert.Single(logs.Lines, l => l.Contains("GET /plan failed", StringComparison.Ordinal));
        Assert.DoesNotContain(leak, line);
        Assert.Contains("[redacted]", line);
    }

    [Fact]
    public async Task A_chunked_body_is_refused_over_the_wire_too()
    {
        var handlers = new Handlers();
        await using var app = await Start(handlers);
        using var http = Client(app);

        var request = new HttpRequestMessage(HttpMethod.Post, "/reconcile") { Content = new StreamContent(new MemoryStream("x"u8.ToArray())) };
        request.Headers.TransferEncodingChunked = true;
        var reply = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Equal(0, handlers.Reconciles);
    }

    [Fact]
    public async Task The_router_is_given_the_raw_target_not_the_framework_decoded_path()
    {
        // Kestrel percent-decodes Request.Path, so `/%73tatus` would arrive as `/status` and be
        // answered — a second spelling of a route the router never agreed to. A raw socket, because
        // HttpClient's Uri may canonicalise the escape before it is ever sent.
        await using var app = await Start(new Handlers());
        using var http = Client(app);
        var port = http.BaseAddress!.Port;
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        await using var stream = tcp.GetStream();
        await stream.WriteAsync("GET /%73tatus HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"u8.ToArray());
        var reply = await new StreamReader(stream).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.StartsWith("HTTP/1.1 404", reply);
    }

    [Fact]
    public async Task The_in_flight_lock_is_shared_across_requests_not_created_per_request()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var handlers = new Handlers { Reconcile = async () => { entered.TrySetResult(); await gate.Task; } };
        await using var app = await Start(handlers);
        using var http = Client(app);

        var first = http.PostAsync("/reconcile", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await http.PostAsync("/reconcile", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    [Fact]
    public async Task The_router_is_one_per_process_and_the_reconciler_one_per_request()
    {
        // Resolved, not just registered: a factory that built a fresh router per resolve would
        // pass a lifetime check and still lose the lock.
        // The cluster client is the one piece that needs a service account to construct, so it
        // alone is stood in for, at the same scoped lifetime; the reconciler resolved is the real one.
        await using var app = ProvisionerServer.Build(["--urls", "http://127.0.0.1:0"], Settings(), s => s.AddScoped<IKubernetesClient, NoCluster>());
        Assert.Same(app.Services.GetRequiredService<IRouter>(), app.Services.GetRequiredService<IRouter>());
        // And registered so: the host resolves the router once from the root, which hides a scoped
        // registration at runtime — until anything resolves it from a request's scope.
        var registered = new ServiceCollection().AddProvisionerServices(Settings());
        Assert.Equal(ServiceLifetime.Singleton, registered.Single(d => d.ServiceType == typeof(IRouter)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, registered.Single(d => d.ServiceType == typeof(IReconciler)).Lifetime);

        using var a = app.Services.CreateScope();
        using var b = app.Services.CreateScope();
        Assert.IsType<Reconciler>(a.ServiceProvider.GetRequiredService<IReconciler>());
        Assert.Same(a.ServiceProvider.GetRequiredService<IReconciler>(), a.ServiceProvider.GetRequiredService<IReconciler>());
        Assert.NotSame(a.ServiceProvider.GetRequiredService<IReconciler>(), b.ServiceProvider.GetRequiredService<IReconciler>());
    }

    [Fact]
    public void The_apps_are_given_the_host_and_port_this_pod_dials()
    {
        var settings = Settings();

        Assert.Equal("pg-postgresql.data.svc.cluster.local", settings.AppHost);
        Assert.Equal(5433, settings.AppPort);
    }

    [Fact]
    public void The_settings_never_print_the_password()
    {
        Assert.DoesNotContain("hunter2", Settings().ToString());
    }

    [Fact]
    public void A_missing_connection_string_stops_the_pod_at_start_and_says_which_variable()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => ProvisionerSettings.FromEnvironment(_ => null));
        Assert.Contains(ProvisionerSettings.ConnectionStringVariable, refusal.Message);
    }

    [Fact]
    public void An_unparseable_connection_string_is_refused_without_quoting_it()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => ProvisionerSettings.FromEnvironment(_ => "Host=pg;Password=hunter2hunter2;Bogus Keyword=1"));
        Assert.DoesNotContain("hunter2", refusal.Message);
    }

    [Fact]
    public void A_connection_string_with_no_host_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ProvisionerSettings.FromEnvironment(_ => "Username=provisioner;Password=x"));
    }

    [Theory]
    [InlineData("deadbeefdeadbeefdeadbeefdeadbeef", "[redacted]")]
    [InlineData("role app PASSWORD '0123456789abcdef0123456789abcdef0123'", "role app PASSWORD '[redacted]'")]
    [InlineData("deadbeef", "deadbeef")]
    public void Redact_masks_any_run_shaped_like_a_generated_value(string text, string expected)
    {
        Assert.Equal(expected, ProvisionerServer.Redact(text));
    }

    private static ProvisionerSettings Settings() => ProvisionerSettings.FromEnvironment(_ => Connection);

    private static async Task<WebApplication> Start(Handlers handlers, CapturedLogs? logs = null)
    {
        var app = ProvisionerServer.Build(["--urls", "http://127.0.0.1:0"], Settings(), s =>
        {
            s.AddSingleton<IProvisionerHandlers>(handlers);
            if (logs is not null)
            {
                s.AddSingleton<ILoggerProvider>(logs);
            }
        });
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };

    private sealed class NoCluster : IKubernetesClient
    {
        public Task<IReadOnlyList<ISecretProbe>> ProbeSecretsAsync(IEnumerable<(string Namespace, string Name)> secrets, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CreateSecretAsync(string ns, string name, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Handlers : IProvisionerHandlers
    {
        private int _reconciles;

        public Func<Task>? Reconcile { get; init; }

        public Action? Plan { get; init; }

        public int Reconciles => Volatile.Read(ref _reconciles);

        public Task<object> StatusAsync(CancellationToken cancellationToken) => Task.FromResult<object>(new { ok = "status" });

        public Task<object> PlanAsync(CancellationToken cancellationToken)
        {
            Plan?.Invoke();
            return Task.FromResult<object>(new { ok = "plan" });
        }

        public async Task<object> ReconcileAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reconciles);
            if (Reconcile is not null)
            {
                await Reconcile();
            }

            return new { ok = "reconcile" };
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
