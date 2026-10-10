namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>The part of a Kubernetes object's metadata this service reads.</summary>
public interface IKubeObjectMeta
{
    string? Name { get; }

    string? Namespace { get; }

    string? ResourceVersion { get; }
}
