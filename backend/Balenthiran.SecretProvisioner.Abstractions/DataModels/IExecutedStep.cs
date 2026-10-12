namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>
/// What a plan step becomes in the report: its identity and its kind, and never a value. Which
/// fields are set depends on <see cref="Kind"/>.
/// </summary>
public interface IExecutedStep
{
    string Kind { get; }

    string Detail { get; }

    /// <summary><c>create-role</c> only.</summary>
    string? Role { get; }

    /// <summary><c>create-database</c> only.</summary>
    string? Database { get; }

    /// <summary><c>create-database</c> only: the role that owns it.</summary>
    string? Owner { get; }

    /// <summary><c>create-secret</c> only.</summary>
    string? Namespace { get; }

    /// <summary><c>create-secret</c> only.</summary>
    string? SecretName { get; }

    /// <summary><c>create-secret</c> only: the key NAMES, never the entries that carry values.</summary>
    IReadOnlyList<string>? Keys { get; }
}
