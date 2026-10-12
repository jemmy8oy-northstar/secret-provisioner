using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>One namespace the observer was asked about: its Secret listing, or the error that stopped it being listed. Exactly one is set.</summary>
public sealed class NamespaceListing : INamespaceListing
{
    [JsonPropertyOrder(1)]
    public required string Namespace { get; init; }

    [JsonPropertyOrder(2)]
    public KubeSecretList? Listing { get; init; }

    [JsonPropertyOrder(3)]
    public string? Error { get; init; }

    IKubeSecretList? INamespaceListing.Listing => Listing;
}
