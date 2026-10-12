using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Services.Clients;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// The real HttpClient from <see cref="KubernetesHttpClientFactory"/> against a real TLS listener on
/// loopback. The validator is unit-tested on its own; this proves the factory actually WIRES it in,
/// which no handler-level fake can: remove the callback and the public store decides, accept-any and
/// the impostor is trusted.
/// </summary>
public class ClusterTlsHandshakeTests
{
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.a-real-looking-service-account-token.signature";

    private static X509Certificate2 MintCa(string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    // A server certificate WITH its private key and a loopback SAN, so the name check passes and the
    // chain is the only thing under test.
    private static X509Certificate2 MintServerCertificate(X509Certificate2 issuer)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=kubernetes", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    // Serves one `{"items":[]}` answer to one connection, and swallows the handshake failure an
    // untrusting client causes.
    private static async Task ServeOnceAsync(TcpListener listener, X509Certificate2 certificate)
    {
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsServerAsync(certificate);

            var buffer = new byte[4096];
            var seen = new StringBuilder();
            while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await tls.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            const string body = """{"items":[]}""";
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            await tls.FlushAsync();
        }
        catch (Exception e) when (e is IOException or AuthenticationException or ObjectDisposedException or SocketException)
        {
            // The client refused the certificate, which is what the negative test wants.
        }
    }

    private static async Task<(KubernetesApiException? Error, int Items)> ListAgainstServerAsync(X509Certificate2 clientCa, X509Certificate2 serverIssuer)
    {
        using var server = MintServerCertificate(serverIssuer);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var served = ServeOnceAsync(listener, server);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var config = InClusterConfig.Read(
                name => name switch { "KUBERNETES_SERVICE_HOST" => "127.0.0.1", "KUBERNETES_SERVICE_PORT_HTTPS" => port, _ => null },
                _ => clientCa.ExportCertificatePem());

            using var http = KubernetesHttpClientFactory.Create(config);
            try
            {
                var listing = await new KubernetesClient(http, Token, new Observer()).ListSecretsAsync("balenthiran");
                await served;
                return (null, listing.Items!.Count);
            }
            catch (KubernetesApiException e)
            {
                return (e, 0);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_server_certified_by_the_cluster_CA_is_reached_through_the_real_client()
    {
        using var ca = MintCa("cluster-ca");
        var (error, items) = await ListAgainstServerAsync(ca, ca);
        Assert.Null(error);
        Assert.Equal(0, items);
    }

    [Fact]
    public async Task A_server_certified_by_anyone_else_is_refused_and_the_token_is_not_handed_over()
    {
        using var ca = MintCa("cluster-ca");
        using var impostor = MintCa("someone-else");
        var (error, _) = await ListAgainstServerAsync(ca, impostor);
        Assert.NotNull(error);
        Assert.Null(error.Status);
        Assert.DoesNotContain(Token, error.Message);
    }
}
