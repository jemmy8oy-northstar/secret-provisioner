namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// Base type for every anticipated, gracefully handled failure — web-template's shape. It
/// carries a stable, machine-readable <see cref="ErrorCode"/> and never an HTTP status: the
/// mapping to a status lives at the WebApi boundary.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string message)
        : base(message)
    {
    }

    protected AppException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>A stable code for this failure kind, so a client can branch without parsing prose.</summary>
    public abstract string ErrorCode { get; }
}
