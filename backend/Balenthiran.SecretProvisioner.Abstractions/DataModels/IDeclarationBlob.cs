namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>One <c>*.json</c> entry of the <c>secrets/</c> listing: what it is called, where it lives, and the URL its body is fetched from.</summary>
public interface IDeclarationBlob
{
    string Name { get; }

    string Path { get; }

    /// <summary>The listing's <c>download_url</c>, already checked to be pinned to the commit the listing was requested at.</summary>
    string Url { get; }
}
