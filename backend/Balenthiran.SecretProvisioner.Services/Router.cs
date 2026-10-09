using System.Globalization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services;

/// <summary>
/// Port of <c>src/router.js</c> (claude-code-bot#97, slice 5). See <see cref="IRouter"/>.
/// </summary>
/// <remarks>
/// WHY UNAUTHENTICATED IS SAFE, AND WHICH PART OF THAT THIS CLASS OWNS. James's argument is that
/// the service is ClusterIP with no Ingress at all, so anyone who can reach it already holds
/// cluster credentials. That is true and it is not the whole argument, because it makes
/// reachability the only control — and a compromised pod in the same cluster is reachable. The
/// rest is this class's job:
/// <list type="bullet">
/// <item><c>/reconcile</c> TAKES NO BODY, and a request carrying one is refused. The declarations
/// in oke-fleet are the only input, so there is no payload through which a caller could smuggle a
/// value, a name or a namespace. A cross-origin <c>text/plain</c> POST needs no preflight, so "a
/// browser cannot call this" is not a control either — refusing the body is.</item>
/// <item>A GET NEVER WRITES. <c>/reconcile</c> answers 405 to GET rather than doing the work, so
/// no link, crawler, probe or prefetch can provision anything.</item>
/// <item>ONE RECONCILE AT A TIME. Two concurrent runs plan against the same observation and then
/// race to create the same objects. Create-only means the loser is refused by the API rather
/// than corrupting anything, so the damage is a report full of 409s and 42710s that looks like a
/// broken cluster. The second caller gets 409 instead.</item>
/// </list>
/// ERRORS DO NOT ECHO. A handler's exception may carry anything — a Postgres error quotes the
/// statement that failed, and that statement contains a password. The body is assembled here from
/// fixed strings, never from an exception's message.
/// </remarks>
public sealed class Router : IRouter
{
    private const int Idle = 0;
    private const int Running = 1;

    /// <summary>A route, as data, so "what routes exist" is one readable list rather than a chain of ifs.</summary>
    public sealed record Route(
        string Method,
        string Path,
        Func<IProvisionerHandlers, CancellationToken, Task<object>> Handler,
        bool Exclusive = false,
        bool RejectsBody = false);

    /// <summary>
    /// Which route, if any, a request is for. <see cref="Allowed"/> is empty when the path is
    /// unknown, which is what separates "no such route" (404) from "wrong method for a real route"
    /// (405). The normalised path comes back too, so the 405 cannot recompute it a second way and
    /// disagree with the match that produced it.
    /// </summary>
    public sealed record RouteMatch(Route? Route, string Path, IReadOnlyList<string> Allowed);

    private static readonly Route[] Routes =
    [
        new("GET", "/status", (h, ct) => h.StatusAsync(ct)),
        new("GET", "/plan", (h, ct) => h.PlanAsync(ct)),
        new("POST", "/reconcile", (h, ct) => h.ReconcileAsync(ct), Exclusive: true, RejectsBody: true),
    ];

    /// <summary>The whole surface, derived from <see cref="Routes"/> so the 404 body cannot drift away from it.</summary>
    public static IReadOnlyList<string> Surface { get; } = Routes.Select(r => $"{r.Method} {r.Path}").ToArray();

    private readonly IProvisionerHandlers _handlers;

    // The one lock shared by every exclusive route. An instance field on a singleton, so "is a
    // reconcile running" is shared across requests: a per-request flag would make `Exclusive`
    // silently do nothing — the guard would exist, pass its own unit test, and never once be true.
    private int _reconcileState = Idle;

    public Router(IProvisionerHandlers handlers)
    {
        _handlers = handlers;
    }

    /// <summary>
    /// The path is compared EXACTLY, after the query string and fragment are discarded. No
    /// trailing-slash aliasing and no prefix matching: <c>/reconcile/</c> and <c>/reconcileX</c>
    /// are not this endpoint, and a surface with one write on it should have exactly one spelling
    /// of that write.
    /// </summary>
    public RouteMatch Match(string method, string? url)
    {
        // The request target is a path, possibly with a query and a fragment. Cutting at both
        // means `/plan?x=1` is `/plan`, and — the part worth stating — that a query string can
        // never reach a handler, so there is no way to pass an argument to an endpoint that is
        // specified as taking none.
        var path = (url ?? string.Empty).Split('#')[0].Split('?')[0];
        var onPath = Routes.Where(r => r.Path == path).ToList();
        return new RouteMatch(
            onPath.FirstOrDefault(r => r.Method == method),
            path,
            onPath.Select(r => r.Method).ToList());
    }

    /// <summary>
    /// Has this request got a body? <c>/reconcile</c> refuses one.
    /// </summary>
    /// <remarks>
    /// Checked from the headers rather than by reading the stream, because reading it is the thing
    /// being refused: a body that is never read cannot be logged, parsed, or mistaken for input by
    /// a later change to this file. Both spellings matter. <c>content-length: 0</c> is not a body,
    /// and a chunked request carries no content-length at all — so a check on either header alone
    /// lets one of the two shapes through.
    /// </remarks>
    public bool CarriesBody(string? contentLength, string? transferEncoding)
    {
        // Mirrors JS `Number(x)`: surrounding whitespace is trimmed, a number is a number whatever
        // its spelling, and anything that is not finite and positive is not a body.
        if (double.TryParse(contentLength, NumberStyles.Float, CultureInfo.InvariantCulture, out var length)
            && double.IsFinite(length)
            && length > 0)
        {
            return true;
        }

        return transferEncoding?.Contains("chunked", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public async Task<IRouteReply> RespondAsync(IRouteRequest request, CancellationToken cancellationToken = default)
    {
        var (route, _, allowed) = Match(request.Method, request.Url);

        if (route is null)
        {
            // 405 names the methods that DO work on this path. Without it, `GET /reconcile` and a
            // typo produce the same 404 and nobody learns that something tried to provision with
            // a GET.
            if (allowed.Count > 0)
            {
                return new RouteReply
                {
                    Status = 405,
                    Body = new MethodNotAllowedBody { Allowed = allowed },
                    Allow = string.Join(", ", allowed),
                };
            }

            return new RouteReply { Status = 404, Body = new NoSuchRouteBody { Routes = Surface } };
        }

        if (route.RejectsBody && CarriesBody(request.ContentLength, request.TransferEncoding))
        {
            return new RouteReply { Status = 400, Body = new BodyRefusedBody() };
        }

        if (route.Exclusive)
        {
            // Check-and-set as ONE atomic operation. In Node this was a plain read then write that
            // was only safe because Node runs a request's synchronous stretch to completion; Kestrel
            // runs requests in parallel on the thread pool, so the same two lines would let two
            // callers both read Idle and both proceed. CompareExchange is the pair made indivisible.
            if (Interlocked.CompareExchange(ref _reconcileState, Running, Idle) != Idle)
            {
                return new RouteReply { Status = 409, Body = new ReconcileBusyBody() };
            }
        }

        try
        {
            return new RouteReply { Status = 200, Body = await route.Handler(_handlers, cancellationToken) };
        }
        catch (Exception error)
        {
            // The handler's message is deliberately NOT in the response (see the class remarks).
            // It is handed back in Thrown so the caller can log it where redaction is that layer's
            // job. A cancelled request lands here too: it is a handler that did not finish.
            return new RouteReply
            {
                Status = 500,
                Body = new HandlerFailedBody { Error = $"{route.Method} {route.Path} failed" },
                Thrown = error,
            };
        }
        finally
        {
            // Released on every exit including a failed run, or one failure is a 409 forever and
            // the only fix is a pod restart.
            if (route.Exclusive)
            {
                Interlocked.Exchange(ref _reconcileState, Idle);
            }
        }
    }
}
