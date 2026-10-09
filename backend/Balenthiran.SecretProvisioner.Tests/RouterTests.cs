using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// The HTTP surface's rules, decided without a socket: what the router refuses, what it says, and
/// that one reconcile runs at a time. Ported from <c>router.test.js</c>; the two cases that drive
/// a real server belong to the host's tests.
/// </summary>
public class RouterTests
{
    private sealed class FakeHandlers : IProvisionerHandlers
    {
        public Func<CancellationToken, Task<object>> Status { get; set; } = _ => Task.FromResult<object>(new { ok = "status" });

        public Func<CancellationToken, Task<object>> Plan { get; set; } = _ => Task.FromResult<object>(new { ok = "plan" });

        public Func<CancellationToken, Task<object>> Reconcile { get; set; } = _ => Task.FromResult<object>(new { ok = "reconcile" });

        public Task<object> StatusAsync(CancellationToken cancellationToken) => Status(cancellationToken);

        public Task<object> PlanAsync(CancellationToken cancellationToken) => Plan(cancellationToken);

        public Task<object> ReconcileAsync(CancellationToken cancellationToken) => Reconcile(cancellationToken);
    }

    private static RouteRequest Req(string method, string url, string? contentLength = null, string? transferEncoding = null) =>
        new() { Method = method, Url = url, ContentLength = contentLength, TransferEncoding = transferEncoding };

    private static string Wire(IRouteReply reply) => JsonSerializer.Serialize(reply.Body, Json.Options);

    private static Router NewRouter(FakeHandlers? handlers = null) => new(handlers ?? new FakeHandlers());

    // ── the surface is exactly three routes ─────────────────────────────────────

    [Fact]
    public void The_surface_is_the_three_routes_James_specified_and_nothing_else()
    {
        Assert.Equal(["GET /status", "GET /plan", "POST /reconcile"], Router.Surface);
    }

    [Fact]
    public async Task An_unknown_path_is_404_and_says_what_does_exist()
    {
        var reply = await NewRouter().RespondAsync(Req("GET", "/secrets"));

        Assert.Equal(404, reply.Status);
        Assert.Equal(
            """{"error":"no such route","routes":["GET /status","GET /plan","POST /reconcile"]}""",
            Wire(reply));
    }

    [Theory]
    [InlineData("/reconcile/")]
    [InlineData("/reconcileX")]
    [InlineData("/x/reconcile")]
    [InlineData("/Reconcile")]
    public async Task The_path_is_matched_EXACTLY_no_trailing_slash_no_prefix(string path)
    {
        // A surface with exactly one write on it should have exactly one spelling of that write.
        var handlers = new FakeHandlers { Reconcile = _ => throw new InvalidOperationException("reached the endpoint") };

        var reply = await NewRouter(handlers).RespondAsync(Req("POST", path));

        Assert.Equal(404, reply.Status);
    }

    [Fact]
    public async Task A_query_string_is_discarded_so_no_argument_can_reach_an_endpoint_specified_as_taking_none()
    {
        var reply = await NewRouter().RespondAsync(Req("GET", "/plan?namespace=balenthiran&force=1"));

        Assert.Equal(200, reply.Status);
        Assert.Equal("/plan", NewRouter().Match("GET", "/plan?x=1#y").Path);
    }

    [Fact]
    public void A_fragment_is_discarded_even_when_it_precedes_a_question_mark()
    {
        Assert.Equal("/plan", NewRouter().Match("GET", "/plan#frag?x=1").Path);
        Assert.Equal(string.Empty, NewRouter().Match("GET", null).Path);
    }

    [Fact]
    public void The_method_is_compared_exactly()
    {
        Assert.Null(NewRouter().Match("get", "/status").Route);
        Assert.NotNull(NewRouter().Match("GET", "/status").Route);
    }

    // ── a GET never writes ──────────────────────────────────────────────────────

    [Fact]
    public async Task GET_reconcile_is_405_and_does_NOT_run_the_handler()
    {
        // The failure this prevents: a link, a crawler, a browser prefetch or an uptime probe
        // provisioning the estate. A 404 here would work just as well for the caller and would
        // hide the fact that it happened.
        var ran = false;
        var handlers = new FakeHandlers { Reconcile = _ => { ran = true; return Task.FromResult<object>(new { }); } };

        var reply = await NewRouter(handlers).RespondAsync(Req("GET", "/reconcile"));

        Assert.Equal(405, reply.Status);
        Assert.False(ran);
        Assert.Equal("""{"error":"method not allowed","allowed":["POST"]}""", Wire(reply));
        Assert.Equal("POST", reply.Allow);
    }

    [Fact]
    public async Task POST_to_a_read_route_is_405_not_a_silent_200()
    {
        var reply = await NewRouter().RespondAsync(Req("POST", "/plan"));

        Assert.Equal(405, reply.Status);
        Assert.Equal("""{"error":"method not allowed","allowed":["GET"]}""", Wire(reply));
    }

    [Fact]
    public async Task A_405_and_a_404_are_different_answers_a_real_path_with_the_wrong_method_must_not_read_as_a_typo()
    {
        Assert.Equal(405, (await NewRouter().RespondAsync(Req("GET", "/reconcile"))).Status);
        Assert.Equal(404, (await NewRouter().RespondAsync(Req("GET", "/reconcil"))).Status);
    }

    [Fact]
    public async Task GET_reconcile_with_a_query_is_still_405_the_405_must_not_derive_the_path_a_second_way()
    {
        // Found by a surviving mutant, not by thinking of it. Recomputing `allowed` from the raw
        // url inside the 405 branch passes every other test here and turns this case into a 404 —
        // and a query string is exactly what a LINK carries, which is the one caller this refusal
        // exists to make visible.
        var reply = await NewRouter().RespondAsync(Req("GET", "/reconcile?from=some-link"));

        Assert.Equal(405, reply.Status);
        Assert.Equal("""{"error":"method not allowed","allowed":["POST"]}""", Wire(reply));
    }

    // ── /reconcile takes no body ────────────────────────────────────────────────

    [Fact]
    public async Task A_body_on_reconcile_is_refused_and_the_handler_does_not_run()
    {
        // This is the property that makes an unauthenticated endpoint safe, and until it is
        // enforced it is only a sentence in the README. No body means no payload through which a
        // caller could smuggle a value, a name or a namespace.
        var ran = false;
        var handlers = new FakeHandlers { Reconcile = _ => { ran = true; return Task.FromResult<object>(new { }); } };

        var reply = await NewRouter(handlers).RespondAsync(Req("POST", "/reconcile", contentLength: "2"));

        Assert.Equal(400, reply.Status);
        Assert.False(ran);
    }

    [Fact]
    public async Task A_chunked_body_on_reconcile_is_refused_through_the_router_too()
    {
        var reply = await NewRouter().RespondAsync(Req("POST", "/reconcile", transferEncoding: "chunked"));

        Assert.Equal(400, reply.Status);
    }

    [Fact]
    public void A_CHUNKED_body_is_refused_too_it_carries_no_content_length_at_all()
    {
        // A check on content-length alone lets this shape straight through, and it is the shape a
        // cross-origin `text/plain` POST can take. "A browser cannot call this" is not a control;
        // refusing the body is.
        var router = NewRouter();

        Assert.True(router.CarriesBody(null, "chunked"));
        Assert.True(router.CarriesBody(null, "Chunked"));
        Assert.True(router.CarriesBody("2", null));
    }

    [Fact]
    public void Content_length_zero_is_NOT_a_body_a_plain_curl_POST_must_work()
    {
        // `curl -X POST` sends `content-length: 0`. Treating that as a body would make the endpoint
        // unusable by the one command anyone would reach for.
        var router = NewRouter();

        Assert.False(router.CarriesBody("0", null));
        Assert.False(router.CarriesBody(null, null));
        Assert.False(router.CarriesBody("0", "identity"));
    }

    [Theory]
    [InlineData(" 2 ", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("garbage", false)]
    [InlineData("Infinity", false)]
    public void Content_length_is_read_as_a_number_the_way_the_JS_did(string contentLength, bool expected)
    {
        Assert.Equal(expected, NewRouter().CarriesBody(contentLength, null));
    }

    [Fact]
    public async Task A_body_is_refused_WITHOUT_being_read()
    {
        // Checked from the headers, never from the stream: a body that is never read cannot be
        // logged, parsed, or mistaken for input by a later edit. The request here has no stream
        // at all, which is the assertion.
        var reply = await NewRouter().RespondAsync(Req("POST", "/reconcile", contentLength: "9"));

        Assert.Equal(400, reply.Status);
        Assert.Contains("only input", Wire(reply));
        Assert.Equal(
            """{"error":"this endpoint takes no body","why":"the declarations in oke-fleet are the only input; a request body could only be an attempt to pass one another way"}""",
            Wire(reply));
    }

    [Fact]
    public async Task A_body_is_only_refused_on_reconcile_the_read_routes_are_not_asked()
    {
        var reply = await NewRouter().RespondAsync(Req("GET", "/status", contentLength: "9"));

        Assert.Equal(200, reply.Status);
    }

    // ── one reconcile at a time ─────────────────────────────────────────────────

    [Fact]
    public async Task A_second_reconcile_while_one_is_running_is_409_not_a_queue_and_not_a_race()
    {
        // Two runs plan against the same observation and then race to create the same objects.
        // Create-only means the loser is refused by the API rather than corrupting anything — so
        // the damage is a report full of "already exists" that reads like a broken cluster.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new FakeHandlers
        {
            Reconcile = async _ => { await gate.Task; return new { ok = "reconcile" }; },
        };
        var router = NewRouter(handlers);

        var first = router.RespondAsync(Req("POST", "/reconcile"));
        // Bounded: without the guard the second call queues behind the gate, and an unbounded
        // await would hang the suite instead of failing it.
        var second = await router.RespondAsync(Req("POST", "/reconcile")).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(409, second.Status);
        Assert.Equal("""{"error":"a reconcile is already running","retry":"when it finishes"}""", Wire(second));

        gate.SetResult();
        Assert.Equal(200, (await first).Status);
    }

    [Fact]
    public async Task The_lock_is_released_after_a_run_including_a_failed_one()
    {
        var handlers = new FakeHandlers { Reconcile = _ => throw new InvalidOperationException("nope") };
        var router = NewRouter(handlers);

        Assert.Equal(500, (await router.RespondAsync(Req("POST", "/reconcile"))).Status);

        // If the lock leaked, this is 409 forever and the only fix is a pod restart.
        handlers.Reconcile = _ => Task.FromResult<object>(new { ok = "reconcile" });
        Assert.Equal(200, (await router.RespondAsync(Req("POST", "/reconcile"))).Status);
    }

    [Fact]
    public async Task The_lock_is_released_after_a_cancelled_run()
    {
        using var cts = new CancellationTokenSource();
        var handlers = new FakeHandlers
        {
            Reconcile = async ct => { await Task.Delay(Timeout.Infinite, ct); return new { }; },
        };
        var router = NewRouter(handlers);

        var running = router.RespondAsync(Req("POST", "/reconcile"), cts.Token);
        await cts.CancelAsync();
        var cancelled = await running;

        Assert.Equal(500, cancelled.Status);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled.Thrown);

        handlers.Reconcile = _ => Task.FromResult<object>(new { ok = "reconcile" });
        Assert.Equal(200, (await router.RespondAsync(Req("POST", "/reconcile"))).Status);
    }

    [Fact]
    public async Task Reads_are_never_locked_status_and_plan_work_while_a_reconcile_runs()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new FakeHandlers
        {
            Reconcile = async _ => { await gate.Task; return new { }; },
        };
        var router = NewRouter(handlers);
        var running = router.RespondAsync(Req("POST", "/reconcile"));

        Assert.Equal(200, (await router.RespondAsync(Req("GET", "/status"))).Status);
        Assert.Equal(200, (await router.RespondAsync(Req("GET", "/plan"))).Status);

        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task A_failed_read_does_not_take_the_reconcile_lock()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlers = new FakeHandlers
        {
            Plan = _ => throw new InvalidOperationException("boom"),
            Reconcile = async _ => { await gate.Task; return new { }; },
        };
        var router = NewRouter(handlers);

        Assert.Equal(500, (await router.RespondAsync(Req("GET", "/plan"))).Status);
        var running = router.RespondAsync(Req("POST", "/reconcile"));
        Assert.False(running.IsCompleted);

        gate.SetResult();
        Assert.Equal(200, (await running).Status);
    }

    [Fact]
    public async Task Thirty_two_reconciles_started_together_yield_exactly_one_200_and_thirty_one_409s()
    {
        // The Node original did check-then-set with nothing awaited between, atomic only because
        // Node is single-threaded. Kestrel runs requests in parallel, so this really races: the
        // handler is held open until every caller has either entered it or been refused, which
        // means a broken lock shows up as more than one caller inside. Repeated, because the
        // window between a plain read and a plain write is a few instructions wide.
        const int callers = 32;
        for (var round = 0; round < 100; round++)
        {
            var entered = 0;
            var finished = 0;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlers = new FakeHandlers
            {
                Reconcile = async _ =>
                {
                    Interlocked.Increment(ref entered);
                    await release.Task;
                    return new { ok = "reconcile" };
                },
            };
            var router = NewRouter(handlers);

            var calls = Enumerable.Range(0, callers)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    var reply = await router.RespondAsync(Req("POST", "/reconcile"));
                    Interlocked.Increment(ref finished);
                    return reply.Status;
                }))
                .ToArray();
            start.SetResult();

            var settled = SpinWait.SpinUntil(
                () => Volatile.Read(ref finished) + Volatile.Read(ref entered) == callers,
                TimeSpan.FromSeconds(30));
            release.SetResult();
            var statuses = await Task.WhenAll(calls);

            Assert.True(settled, $"round {round}: callers never all arrived");
            Assert.Equal(1, Volatile.Read(ref entered));
            Assert.Equal(1, statuses.Count(s => s == 200));
            Assert.Equal(callers - 1, statuses.Count(s => s == 409));
        }
    }

    // ── errors do not echo ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_handlers_error_message_never_reaches_the_response_body()
    {
        // A Postgres error quotes the statement that failed, and that statement contains a
        // password. An exception escaping before any redaction has been through nothing.
        const string leak = "CREATE ROLE app PASSWORD 'deadbeefdeadbeef'";
        var handlers = new FakeHandlers { Reconcile = _ => throw new InvalidOperationException($"syntax error at {leak}") };

        var reply = await NewRouter(handlers).RespondAsync(Req("POST", "/reconcile"));

        Assert.Equal(500, reply.Status);
        Assert.DoesNotContain("deadbeef", Wire(reply));
        Assert.DoesNotContain("PASSWORD", Wire(reply));
        Assert.Equal("""{"error":"POST /reconcile failed","detail":"see the pod log"}""", Wire(reply));
    }

    [Fact]
    public async Task The_error_is_still_handed_to_the_caller_to_log_refusing_to_echo_is_not_swallowing()
    {
        var thrown = new InvalidOperationException("boom");
        var handlers = new FakeHandlers { Status = _ => throw thrown };

        var reply = await NewRouter(handlers).RespondAsync(Req("GET", "/status"));

        Assert.Same(thrown, reply.Thrown);
        Assert.Equal("""{"error":"GET /status failed","detail":"see the pod log"}""", Wire(reply));
    }

    [Fact]
    public async Task A_successful_reply_carries_the_handlers_own_body_and_no_exception()
    {
        var reply = await NewRouter().RespondAsync(Req("GET", "/status"));

        Assert.Equal(200, reply.Status);
        Assert.Equal("""{"ok":"status"}""", Wire(reply));
        Assert.Null(reply.Thrown);
        Assert.Null(reply.Allow);
    }

    [Fact]
    public async Task Each_route_calls_its_own_handler()
    {
        Assert.Equal("""{"ok":"status"}""", Wire(await NewRouter().RespondAsync(Req("GET", "/status"))));
        Assert.Equal("""{"ok":"plan"}""", Wire(await NewRouter().RespondAsync(Req("GET", "/plan"))));
        Assert.Equal("""{"ok":"reconcile"}""", Wire(await NewRouter().RespondAsync(Req("POST", "/reconcile"))));
    }

    [Fact]
    public void A_reply_never_serialises_its_exception()
    {
        var reply = new RouteReply
        {
            Status = 500,
            Body = new { },
            Thrown = new InvalidOperationException("PASSWORD 'deadbeef'"),
        };

        Assert.DoesNotContain("deadbeef", JsonSerializer.Serialize(reply, Json.Options));
    }
}
