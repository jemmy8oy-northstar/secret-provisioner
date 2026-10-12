namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>A declared key the plan does not act on, and the reason. <see cref="Action"/> is set only on blocked entries and names the human step.</summary>
public interface IPlanEntry
{
    string App { get; }

    string Namespace { get; }

    string SecretName { get; }

    string Key { get; }

    /// <summary>Absent on unmanaged keys: no declaration says what they are.</summary>
    string? Type { get; }

    string? Action { get; }

    string Reason { get; }
}
