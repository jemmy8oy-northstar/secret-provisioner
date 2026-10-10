namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The <c>secrets/</c> listing cannot be trusted as a complete, single-commit set of
/// declarations: not an array, nothing in it, an entry that is not a plain file, or a download
/// URL that is missing or not pinned to the resolved commit. Never read as "no declarations".
/// </summary>
public sealed class InvalidDeclarationListingException : AppException
{
    public InvalidDeclarationListingException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "invalid_declaration_listing";
}
