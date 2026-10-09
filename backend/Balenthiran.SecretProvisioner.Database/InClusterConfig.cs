using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;

namespace Balenthiran.SecretProvisioner.Database;

/// <summary>
/// The in-cluster connection details, from the kubelet's mount and the environment. Port of
/// <c>inClusterConfig</c> in <c>src/kube.js</c>.
/// </summary>
/// <remarks>
/// Every field is required and nothing is defaulted. A default here is a client that silently
/// talks to the wrong cluster, or to no cluster, and reports the result as an observation — and an
/// observation that is wrong about what it enumerated is the one failure this whole service is
/// arranged around.
/// </remarks>
public sealed class InClusterConfig
{
    /// <summary>Where the kubelet mounts the pod's identity. Not configurable; it is a kubelet contract.</summary>
    public const string ServiceAccountDirectory = "/var/run/secrets/kubernetes.io/serviceaccount";

    private static readonly JsonSerializerOptions Quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private InClusterConfig(string host, int port, string token, string ca)
    {
        Host = host;
        Port = port;
        Token = token;
        Ca = ca;
    }

    public string Host { get; }

    public int Port { get; }

    /// <summary>The service-account token, trimmed. ⚠️ A credential: never log it, never put it in a message.</summary>
    public string Token { get; }

    /// <summary>The cluster CA, PEM. The only trust anchor the client will accept.</summary>
    public string Ca { get; }

    /// <summary>Read the configuration from the real environment and filesystem.</summary>
    public static InClusterConfig FromEnvironment() => Read(Environment.GetEnvironmentVariable, File.ReadAllText);

    /// <summary>Read the configuration through injectable environment and file readers.</summary>
    public static InClusterConfig Read(Func<string, string?> env, Func<string, string> read)
    {
        var host = env("KUBERNETES_SERVICE_HOST");
        var port = env("KUBERNETES_SERVICE_PORT_HTTPS") ?? env("KUBERNETES_SERVICE_PORT") ?? "443";

        if (string.IsNullOrEmpty(host))
        {
            throw new InClusterConfigException("KUBERNETES_SERVICE_HOST is not set — this process is not running in a cluster, and there is no sensible default for which cluster to provision");
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var portNumber) || portNumber < 1 || portNumber > 65535)
        {
            throw new InClusterConfigException($"KUBERNETES_SERVICE_PORT must be an integer from 1 to 65535, got {JsonSerializer.Serialize(port, Quote)}");
        }

        string token;
        string ca;
        try
        {
            token = read($"{ServiceAccountDirectory}/token").Trim();
            ca = read($"{ServiceAccountDirectory}/ca.crt");
        }
        catch (Exception e)
        {
            throw new InClusterConfigException($"cannot read the service account at {ServiceAccountDirectory}: {e.Message}. The Deployment must not set automountServiceAccountToken: false");
        }

        if (token == string.Empty)
        {
            throw new InClusterConfigException($"the service account token at {ServiceAccountDirectory}/token is empty");
        }

        // Refused rather than handed to the TLS layer, where an empty trust anchor can degrade to
        // the PUBLIC root store — so an empty file would turn "verify against the cluster CA" into
        // "verify against the internet", which fails in a way that looks like a network problem
        // rather than like the authentication decision it actually is.
        if (string.IsNullOrWhiteSpace(ca))
        {
            throw new InClusterConfigException($"the cluster CA at {ServiceAccountDirectory}/ca.crt is empty — refusing to fall back to the public root store");
        }

        return new InClusterConfig(host, portNumber, token, ca);
    }
}
