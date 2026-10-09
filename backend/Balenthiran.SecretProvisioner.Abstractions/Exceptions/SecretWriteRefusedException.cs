namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The client declined to write a Secret key because doing so could overwrite a value it did not
/// create, or could not be made conditional. Nothing was sent.
/// </summary>
public sealed class SecretWriteRefusedException : AppException
{
    public SecretWriteRefusedException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "secret_write_refused";
}
