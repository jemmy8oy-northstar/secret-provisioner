namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The executor will not do what it was asked: the plan contradicts itself or the declarations, a
/// value cannot be generated, or a connection string would need quoting. Raised inside a step, so
/// it lands in the report as that step's failure rather than escaping.
/// </summary>
public sealed class ExecutionRefusedException : AppException
{
    public ExecutionRefusedException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "execution_refused";
}
