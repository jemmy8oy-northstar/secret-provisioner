namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The branch to read was not named literally (empty, or <c>HEAD</c>). Refused rather than
/// resolved: <c>HEAD</c> means "the default branch", which is the branch Argo CD releases from,
/// and reading it would collapse the ordering that makes provisioning precede release.
/// </summary>
public sealed class InvalidDeclarationSourceException : AppException
{
    public InvalidDeclarationSourceException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "invalid_declaration_source";
}
