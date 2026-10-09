using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Database;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>The TLS decision: the cluster CA is the only trust anchor. CAs and leaves are minted in-test.</summary>
public class ClusterCertificateValidatorTests
{
    private static X509Certificate2 MintCa(string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 MintLeaf(X509Certificate2 issuer)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=kubernetes", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var leaf = request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(8));
        return X509CertificateLoader.LoadCertificate(leaf.RawData);
    }

    [Fact]
    public void A_certificate_signed_by_the_configured_CA_is_trusted()
    {
        using var ca = MintCa("cluster-ca");
        using var leaf = MintLeaf(ca);
        var validator = new ClusterCertificateValidator(ca.ExportCertificatePem());
        Assert.True(validator.IsTrusted(leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void A_certificate_signed_by_a_different_CA_is_rejected()
    {
        using var ca = MintCa("cluster-ca");
        using var other = MintCa("someone-else");
        using var leaf = MintLeaf(other);
        var validator = new ClusterCertificateValidator(ca.ExportCertificatePem());
        Assert.False(validator.IsTrusted(leaf, null, SslPolicyErrors.None));
    }

    [Fact]
    public void A_self_signed_certificate_that_is_not_the_CA_is_rejected()
    {
        using var ca = MintCa("cluster-ca");
        using var impostor = MintCa("cluster-ca");
        var validator = new ClusterCertificateValidator(ca.ExportCertificatePem());
        Assert.False(validator.IsTrusted(impostor, null, SslPolicyErrors.None));
    }

    [Fact]
    public void A_name_mismatch_or_a_missing_certificate_is_rejected_even_when_the_chain_is_good()
    {
        using var ca = MintCa("cluster-ca");
        using var leaf = MintLeaf(ca);
        var validator = new ClusterCertificateValidator(ca.ExportCertificatePem());
        Assert.False(validator.IsTrusted(leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(validator.IsTrusted(leaf, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(validator.IsTrusted(null, null, SslPolicyErrors.None));
    }

    [Fact]
    public void The_factory_builds_a_thirty_second_client_for_the_configured_host_and_port()
    {
        using var ca = MintCa("cluster-ca");
        var config = InClusterConfig.Read(
            name => name switch { "KUBERNETES_SERVICE_HOST" => "10.96.0.1", "KUBERNETES_SERVICE_PORT_HTTPS" => "6443", _ => null },
            _ => ca.ExportCertificatePem());

        using var client = KubernetesHttpClientFactory.Create(config);

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.Equal("https://10.96.0.1:6443/", client.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public void A_CA_with_no_certificate_in_it_is_refused()
    {
        Assert.Throws<InClusterConfigException>(() => new ClusterCertificateValidator("not a certificate"));
    }
}
