using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;

namespace Balenthiran.SecretProvisioner.Database;

/// <summary>
/// Decides whether the certificate an API server presented chains to the cluster CA — and to
/// nothing else.
/// </summary>
/// <remarks>
/// THE CLUSTER CA IS THE ONLY TRUST ANCHOR. The chain is rebuilt here with
/// <see cref="X509ChainTrustMode.CustomRootTrust"/>, so the operating system's public store plays
/// no part: a certificate any public CA would sign is refused. The chain the TLS layer hands over is
/// used only for the intermediates the server sent, never for its verdict (that one was built
/// against the system store and would say "untrusted root" about a perfectly good cluster).
/// Never "accept any": turning validation off is the standard way a self-signed-certificate error
/// gets "fixed", and here it would mean this pod hands its token to whatever answers on that address.
/// </remarks>
public sealed class ClusterCertificateValidator
{
    private readonly X509Certificate2Collection roots = [];

    public ClusterCertificateValidator(string caPem)
    {
        roots.ImportFromPem(caPem);
        if (roots.Count == 0)
        {
            throw new InClusterConfigException("the cluster CA holds no certificate — refusing to fall back to the public root store");
        }
    }

    /// <summary>Signature-compatible with <see cref="RemoteCertificateValidationCallback"/> once the sender is dropped.</summary>
    public bool IsTrusted(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        // Name and presence are the TLS layer's findings and stand. Chain errors are not: that
        // verdict is against the wrong store, so it is rebuilt below.
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }
}
