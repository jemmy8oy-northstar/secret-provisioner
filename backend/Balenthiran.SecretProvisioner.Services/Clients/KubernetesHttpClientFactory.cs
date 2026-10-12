using System.Net.Http;

namespace Balenthiran.SecretProvisioner.Services.Clients;

/// <summary>
/// Builds the real <see cref="HttpClient"/> for the API server: deliberately the dullest code in the
/// repo. Everything with a decision in it — the method, the path, the content type, the
/// preconditions — lives in <see cref="KubernetesClient"/> above an injectable handler, where tests
/// assert it.
/// </summary>
public static class KubernetesHttpClientFactory
{
    /// <summary>A hung API call must not hold a reconcile open forever.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static HttpClient Create(InClusterConfig config)
    {
        var validator = new ClusterCertificateValidator(config.Ca);
        var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, chain, errors) => validator.IsTrusted(certificate, chain, errors) },
        };

        return new HttpClient(handler)
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttps, config.Host, config.Port).Uri,
            Timeout = DefaultTimeout,
        };
    }
}
