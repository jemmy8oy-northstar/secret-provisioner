namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>Where the declarations were read from, without the declarations: the audit trail a response carries.</summary>
public interface IDeclarationRef
{
    string Owner { get; }

    string Repo { get; }

    string Branch { get; }

    /// <summary>The 40-hex commit every file was read at.</summary>
    string Sha { get; }
}
