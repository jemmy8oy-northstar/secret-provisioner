namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// One fetched declaration is not JSON or lacks a field the provisioner itself dereferences.
/// The message always starts with the file it came from.
/// </summary>
public sealed class InvalidDeclarationException : AppException
{
    public InvalidDeclarationException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "invalid_declaration";
}
