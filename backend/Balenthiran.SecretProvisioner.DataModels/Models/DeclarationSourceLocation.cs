using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>Where the declarations live: a repository, a branch named literally, and the directory under it.</summary>
public sealed class DeclarationSourceLocation : IDeclarationSourceLocation
{
    /// <summary>
    /// <c>oke-fleet:secrets/</c> on <c>dev</c>. <c>dev</c>, not <c>main</c>, is the point: Argo CD releases
    /// from <c>main</c>, so reading <c>dev</c> is what makes provisioning a precondition of release.
    /// </summary>
    public static DeclarationSourceLocation OkeFleet { get; } = new()
    {
        Owner = "jemmy8oy-northstar",
        Repo = "oke-fleet",
        Branch = "dev",
        Dir = "secrets",
    };

    [JsonPropertyOrder(1)]
    public required string Owner { get; init; }

    [JsonPropertyOrder(2)]
    public required string Repo { get; init; }

    [JsonPropertyOrder(3)]
    public required string Branch { get; init; }

    [JsonPropertyOrder(4)]
    public required string Dir { get; init; }
}
