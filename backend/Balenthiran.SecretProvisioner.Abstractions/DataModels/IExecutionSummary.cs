namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>Counts of each report bucket.</summary>
public interface IExecutionSummary
{
    int Done { get; }

    /// <summary>0 or 1: the first failure stops the run.</summary>
    int Failed { get; }

    int Skipped { get; }

    /// <summary>How many values this run generated. A count, never a value; it lets a test see that each one was registered for redaction.</summary>
    int Generated { get; }

    int Blocked { get; }

    int Unknown { get; }

    int Drift { get; }

    int Unmanaged { get; }

    int Noop { get; }
}
