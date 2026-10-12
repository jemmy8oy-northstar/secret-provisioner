namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>
/// One declared Secret, asked about by a dry-run create: does it exist? Exactly one of
/// <see cref="Exists"/> and <see cref="Error"/> is set. Nothing about the Secret's contents is
/// here, because nothing about them was ever fetched.
/// </summary>
public interface ISecretProbe
{
    string Namespace { get; }

    string Name { get; }

    bool? Exists { get; }

    string? Error { get; }
}
