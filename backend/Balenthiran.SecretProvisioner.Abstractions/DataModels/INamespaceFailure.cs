namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>A namespace in which a Secret probe failed, and why.</summary>
public interface INamespaceFailure
{
    string Namespace { get; }

    string Error { get; }
}
