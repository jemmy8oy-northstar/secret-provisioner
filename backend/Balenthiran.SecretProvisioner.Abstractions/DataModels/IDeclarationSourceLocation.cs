namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>Where the declarations live: a repository, a branch named literally, and the directory under it.</summary>
public interface IDeclarationSourceLocation
{
    string Owner { get; }

    string Repo { get; }

    /// <summary>Never <c>HEAD</c>: see <see cref="Services.IDeclarationParser.AssertBranch"/>.</summary>
    string Branch { get; }

    string Dir { get; }
}
