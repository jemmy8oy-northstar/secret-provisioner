using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Microsoft.AspNetCore.Http.Features;
using IRouter = Balenthiran.SecretProvisioner.Abstractions.Services.IRouter;

namespace Balenthiran.SecretProvisioner.WebApi;

/// <summary>
/// The host: hands every request's method, RAW target and body-describing headers to
/// <see cref="IRouter"/>, and delivers what it returns. One catch-all rather than an endpoint per
/// route, as in Kit, so every rule lives in the router where a test decides it, and the framework
/// cannot add an answer of its own — a 404 page, a redirect, a trailing-slash alias — that the
/// router never gave.
/// </summary>
/// <remarks>
/// THE BODY IS NEVER READ. The router refuses one from the headers alone; reading the stream here
/// would be the very thing it refuses, and a body that is never read cannot be logged or mistaken
/// for input by a later edit.
///
/// THE LOG IS REDACTED TOO. A handler's exception never reaches the response — the router sees to
/// that — but it does reach the pod log, and a Postgres error can quote the statement that failed.
/// Every value this service generates is hex of at least 32 characters, so any such run is masked
/// before it is written: the executor's own rule, applied at the last door out.
/// </remarks>
public static class ProvisionerServer
{
    private static readonly Regex SecretShaped = new("[0-9a-f]{32,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <param name="configure">Registers services BEFORE the provisioner's own, which only fill what is missing — so a test can supply fake handlers.</param>
    public static WebApplication Build(string[] args, ProvisionerSettings settings, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
        configure?.Invoke(builder.Services);
        builder.Services.AddProvisionerServices(settings);
        var app = builder.Build();

        var router = app.Services.GetRequiredService<IRouter>();
        var log = app.Services.GetRequiredService<ILogger<WebApplication>>();

        app.Run(async ctx =>
        {
            var request = new RouteRequest
            {
                Method = ctx.Request.Method,
                Url = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget,
                ContentLength = Header(ctx.Request.Headers["Content-Length"]),
                TransferEncoding = Header(ctx.Request.Headers.TransferEncoding),
            };

            var reply = await router.RespondAsync(request, ctx.RequestAborted);
            if (reply.Thrown is not null)
            {
                log.LogError("{Method} {Path} failed: {Type}: {Message}", request.Method, ctx.Request.Path, reply.Thrown.GetType().FullName, Redact(reply.Thrown.Message));
            }

            ctx.Response.StatusCode = reply.Status;
            ctx.Response.ContentType = "application/json";
            if (reply.Allow is not null)
            {
                ctx.Response.Headers.Allow = reply.Allow;
            }

            await ctx.Response.WriteAsync(JsonSerializer.Serialize(reply.Body, reply.Body.GetType(), WireOptions));
        });

        return app;
    }

    /// <summary>Mask anything shaped like a generated value. Public so the rule is tested where it is defined.</summary>
    public static string Redact(string text) => SecretShaped.Replace(text, "[redacted]");

    private static string? Header(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 0 ? null : string.Join(", ", values.ToArray());
}
