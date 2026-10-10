namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>One namespace the observer was asked about: its Secret listing, or the error that stopped it being listed. Exactly one is set.</summary>
public interface INamespaceListing
{
    string Namespace { get; }

    IKubeSecretList? Listing { get; }

    string? Error { get; }
}
