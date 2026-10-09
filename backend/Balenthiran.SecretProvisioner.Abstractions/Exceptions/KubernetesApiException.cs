namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The Kubernetes API did not do what was asked: it answered non-2xx, answered 2xx with something
/// that is not what the route returns, or could not be reached at all.
/// </summary>
/// <remarks>
/// <see cref="Reason"/> is carried as a property so a caller can branch on <c>AlreadyExists</c> or
/// <c>Conflict</c> without matching on English. The message is already redacted of the
/// service-account token; a non-2xx body may be quoted into it (a <c>v1.Status</c>, no Secret
/// values) and a 2xx body never is.
/// </remarks>
public sealed class KubernetesApiException : AppException
{
    public KubernetesApiException(string message, int? status = null, string? reason = null)
        : base(message)
    {
        Status = status;
        Reason = reason;
    }

    /// <summary>The HTTP status, or null when the request never got an answer (DNS, TLS, timeout).</summary>
    public int? Status { get; }

    /// <summary>The <c>reason</c> of the <c>v1.Status</c> the API answered with, when it sent one.</summary>
    public string? Reason { get; }

    public override string ErrorCode => "kubernetes_api_error";
}
