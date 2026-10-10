namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>A namespace whose Secret listing failed, and why.</summary>
public interface INamespaceFailure
{
    string Namespace { get; }

    string Error { get; }
}
