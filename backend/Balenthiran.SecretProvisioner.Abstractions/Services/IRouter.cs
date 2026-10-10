using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// The HTTP surface: exactly three routes, and the rules that make an unauthenticated one safe.
/// Decides the whole reply for one request; the host does nothing but plumbing.
/// </summary>
/// <remarks>
/// <c>GET /status</c>, <c>GET /plan</c>, <c>POST /reconcile</c> — the shape James settled on
/// claude-code-bot#97. The reply is data, and serialising it is the host's job in one place.
/// </remarks>
public interface IRouter
{
    /// <summary>
    /// Respond. Never throws for a handler failure — that becomes a 500 carrying the exception in
    /// <see cref="IRouteReply.Thrown"/> for the caller to log.
    /// </summary>
    Task<IRouteReply> RespondAsync(IRouteRequest request, CancellationToken cancellationToken = default);
}
