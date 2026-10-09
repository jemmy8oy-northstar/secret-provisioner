namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>A <c>v1.SecretList</c> as the Kubernetes API returns it.</summary>
public interface IKubeSecretList
{
    IReadOnlyList<IKubeSecret>? Items { get; }
}
