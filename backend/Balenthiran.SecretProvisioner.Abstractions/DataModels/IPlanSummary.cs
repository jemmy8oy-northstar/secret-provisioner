namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>Counts of each bucket and each step kind.</summary>
public interface IPlanSummary
{
    int Steps { get; }

    int CreateSecret { get; }

    int CreateRole { get; }

    int CreateDatabase { get; }

    int Blocked { get; }

    int Unknown { get; }

    int Drift { get; }

    int Unmanaged { get; }

    int Noop { get; }
}
