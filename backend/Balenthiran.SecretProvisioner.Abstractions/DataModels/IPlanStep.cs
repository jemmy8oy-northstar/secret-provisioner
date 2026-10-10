namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>One action. Which fields are set depends on <see cref="Kind"/>: <c>create-role</c>, <c>create-database</c>, <c>create-secret</c> or <c>patch-secret-key</c>.</summary>
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

    /// <summary><c>patch-secret-key</c> only: the keys already there, which the patch must not disturb.</summary>
    IReadOnlyList<string>? Preserves { get; }

    string Detail { get; }
}
