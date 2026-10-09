namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>The declarations as read at one commit, and which commit — the audit trail for everything a reconcile does.</summary>
public interface IDeclarationSet
{
    string Owner { get; }

    string Repo { get; }

    string Branch { get; }

    /// <summary>The 40-hex commit every file was read at.</summary>
    string Sha { get; }

    IReadOnlyList<ISecretDeclaration> Declarations { get; }
}
