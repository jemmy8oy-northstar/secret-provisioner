using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>One <c>*.json</c> entry of the <c>secrets/</c> listing: what it is called, where it lives, and the URL its body is fetched from.</summary>
public sealed class DeclarationBlob : IDeclarationBlob
{
    [JsonPropertyOrder(1)]
    public required string Name { get; init; }

    [JsonPropertyOrder(2)]
    public required string Path { get; init; }

    /// <summary>The listing's <c>download_url</c>, already checked to be pinned to the commit the listing was requested at.</summary>
    [JsonPropertyOrder(3)]
    public required string Url { get; init; }
}
