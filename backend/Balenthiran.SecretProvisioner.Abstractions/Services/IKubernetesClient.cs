using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// The Kubernetes edge: the only code that talks to the API server. Four calls, over two URL
/// shapes, with the pod's service-account token — no client library.
/// </summary>
/// <remarks>
/// CREATE-ONLY IS ENFORCED BY THE SERVER. <see cref="CreateSecretAsync"/> must be a CREATE, which
/// the API server refuses with 409 if the Secret appeared since it was observed — never an apply
/// or a replace. <see cref="PatchSecretKeyAsync"/> must be unable to overwrite: it refuses a key
/// that is already present and pins the patch to the <c>resourceVersion</c> it read, so a Secret
/// that changed in between is a 409, not a lost write. A check-then-act here would be a race;
/// letting the server refuse is not.
/// </remarks>
public interface IKubernetesClient
{
    /// <summary>Every Secret in <paramref name="ns"/>. Throws on any non-2xx answer.</summary>
    Task<IKubeSecretList> ListSecretsAsync(string ns, CancellationToken cancellationToken = default);

    /// <summary>
    /// List each namespace once, in order. A namespace whose listing failed comes back with its
    /// error and no listing — never omitted, never empty.
    /// </summary>
    Task<IReadOnlyList<INamespaceListing>> ObserveNamespacesAsync(IEnumerable<string> namespaces, CancellationToken cancellationToken = default);

    /// <summary>Create a new Opaque Secret holding <paramref name="data"/>. A 409 is a failure, never retried as an update.</summary>
    Task CreateSecretAsync(string ns, string name, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default);

    /// <summary>Add ONE key to an existing Secret, preserving every other key. Refuses a key already present.</summary>
    Task PatchSecretKeyAsync(string ns, string name, string key, string value, CancellationToken cancellationToken = default);
}
