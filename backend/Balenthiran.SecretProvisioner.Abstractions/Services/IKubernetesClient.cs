using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// The Kubernetes edge: the only code that talks to the API server. One verb, <c>create</c>, used
/// two ways, with the pod's service-account token — no client library.
/// </summary>
/// <remarks>
/// CREATE-ONLY IS ENFORCED BY THE CLUSTER, NOT JUST THE CODE (James, secret-provisioner#9). The
/// Role grants <c>create</c> on Secrets and nothing else, so this pod cannot read a Secret's values,
/// cannot change a Secret, and cannot delete one — whatever this code does. "Does it exist?" is
/// asked with a dry-run create, which the API server answers 409 for an existing name without
/// writing anything and without returning the existing Secret.
/// </remarks>
public interface IKubernetesClient
{
    /// <summary>
    /// Ask about each Secret once, in order. A probe that failed comes back with its error and no
    /// answer — never omitted, never read as "absent".
    /// </summary>
    Task<IReadOnlyList<ISecretProbe>> ProbeSecretsAsync(IEnumerable<(string Namespace, string Name)> secrets, CancellationToken cancellationToken = default);

    /// <summary>Create a new Opaque Secret holding <paramref name="data"/>. A 409 is a failure, never retried as an update.</summary>
    Task CreateSecretAsync(string ns, string name, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default);
}
