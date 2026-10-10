using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>A <c>v1.SecretList</c> as the Kubernetes API returns it.</summary>
public sealed class KubeSecretList : IKubeSecretList
{
    [JsonPropertyOrder(1)]
    public List<KubeSecret>? Items { get; init; }

    IReadOnlyList<IKubeSecret>? IKubeSecretList.Items => Items;
}
