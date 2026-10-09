namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>What a provisioner would do, and everything it will not, sorted into the bucket that says why.</summary>
public interface IPlan
{
    /// <summary>Ordered and executable-shaped: Postgres DDL before the writes that name it.</summary>
    IReadOnlyList<IPlanStep> Steps { get; }

    /// <summary>A human must act; the rules forbid the provisioner from doing so.</summary>
    IReadOnlyList<IPlanEntry> Blocked { get; }

    /// <summary>Undecidable, because the observation did not cover it.</summary>
    IReadOnlyList<IPlanEntry> Unknown { get; }

    /// <summary>Present and inconsistent — reported, never acted on.</summary>
    IReadOnlyList<IPlanEntry> Drift { get; }

    /// <summary>Keys in a declared Secret that no declaration claims. Never deleted.</summary>
    IReadOnlyList<IPlanEntry> Unmanaged { get; }

    /// <summary>Already as declared.</summary>
    IReadOnlyList<IPlanEntry> Noop { get; }

    IPlanSummary Summary { get; }
}
