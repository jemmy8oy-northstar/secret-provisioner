using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// Reads the declarations from <c>oke-fleet/secrets/*.json</c> on <c>dev</c>, pinned to ONE
/// commit: the branch is resolved to a SHA first and every file is read at that SHA, so a push
/// mid-read cannot hand back half of two estates. A partial read is not a smaller estate — one
/// failed file fails the whole load.
/// </summary>
public interface IDeclarationSource
{
    Task<IDeclarationSet> LoadAsync(CancellationToken cancellationToken = default);
}
