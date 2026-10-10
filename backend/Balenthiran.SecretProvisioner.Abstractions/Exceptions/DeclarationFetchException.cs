namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// A read from GitHub failed: a non-2xx answer, an unreachable host, a branch that did not
/// resolve to a commit SHA, or a listing that was not JSON. One failed read fails the whole load.
/// </summary>
public sealed class DeclarationFetchException : AppException
{
    public DeclarationFetchException(string message)
        : base(message)
    {
    }

    public DeclarationFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorCode => "declaration_fetch_failed";
}
