using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>The part of a Kubernetes object's metadata this service reads.</summary>
public sealed class KubeObjectMeta : IKubeObjectMeta
{
    [JsonPropertyOrder(1)]
    public string? Name { get; init; }

    [JsonPropertyOrder(2)]
    public string? Namespace { get; init; }

    [JsonPropertyOrder(3)]
    public string? ResourceVersion { get; init; }
}
