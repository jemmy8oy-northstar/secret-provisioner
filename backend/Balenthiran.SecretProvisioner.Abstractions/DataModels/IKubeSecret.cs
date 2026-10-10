namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>A <c>v1.Secret</c> as the Kubernetes API returns it. ⚠️ <see cref="Data"/> holds VALUES: only the observer reads it, and only for its keys.</summary>
public interface IKubeSecret
{
    IKubeObjectMeta? Metadata { get; }

    IReadOnlyDictionary<string, string?>? Data { get; }

    IReadOnlyDictionary<string, string?>? StringData { get; }
}
