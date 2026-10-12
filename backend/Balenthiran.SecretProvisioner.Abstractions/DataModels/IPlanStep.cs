namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>One action. Which fields are set depends on <see cref="Kind"/>: <c>create-role</c>, <c>create-database</c> or <c>create-secret</c>.</summary>
public interface IPlanStep
{
    string Kind { get; }

    string? App { get; }

    string Namespace { get; }

    string SecretName { get; }

    string? Key { get; }

    string? Type { get; }

    string? Database { get; }

    string? Role { get; }

    string? Source { get; }

    /// <summary><c>create-secret</c> only: every key the new Secret is created with.</summary>
    IReadOnlyList<IStepKey>? Keys { get; }

    string Detail { get; }
}
