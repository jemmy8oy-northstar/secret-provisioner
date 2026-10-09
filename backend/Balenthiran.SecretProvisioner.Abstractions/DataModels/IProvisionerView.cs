namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>
/// One response from the service. <see cref="Source"/> is always there, because every answer is
/// only true of the commit it was read at; the rest is filled as far as the call went.
/// </summary>
public interface IProvisionerView
{
    /// <summary>Which declarations, at which commit.</summary>
    IDeclarationRef Source { get; }

    /// <summary>What was looked at. Never a value.</summary>
    IObservation Observation { get; }

    /// <summary>Null for <c>/status</c>.</summary>
    IPlan? Plan { get; }

    /// <summary>Only for <c>/reconcile</c>.</summary>
    IExecutionReport? Report { get; }
}
