namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>The step that stopped the run, and why.</summary>
public interface IFailedStep
{
    IExecutedStep Step { get; }

    /// <summary>Already redacted: an error can quote the failing statement, password included.</summary>
    string Error { get; }
}
