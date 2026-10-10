using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>A <c>v1.Secret</c> as the Kubernetes API returns it. ⚠️ <see cref="Data"/> holds VALUES: only the observer reads it, and only for its keys.</summary>
public sealed class KubeSecret : IKubeSecret
{
    [JsonPropertyOrder(1)]
    public KubeObjectMeta? Metadata { get; init; }

    [JsonPropertyOrder(2)]
    public Dictionary<string, string?>? Data { get; init; }

    [JsonPropertyOrder(3)]
    public Dictionary<string, string?>? StringData { get; init; }

    IKubeObjectMeta? IKubeSecret.Metadata => Metadata;
    IReadOnlyDictionary<string, string?>? IKubeSecret.Data => Data;
    IReadOnlyDictionary<string, string?>? IKubeSecret.StringData => StringData;
}
