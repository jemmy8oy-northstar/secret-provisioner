using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Services.Clients;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>Port of the configuration half of <c>src/kube.test.js</c>.</summary>
public class InClusterConfigTests
{
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.a-real-looking-service-account-token.signature";
    private const string Ca = "-----BEGIN CERTIFICATE-----\nx\n-----END CERTIFICATE-----\n";
    private const string Dir = "/var/run/secrets/kubernetes.io/serviceaccount";

    private static Func<string, string?> Env(params (string, string)[] pairs) =>
        name => pairs.Where(p => p.Item1 == name).Select(p => p.Item2).FirstOrDefault();

    private static Func<string, string> Files(string token, string ca) =>
        path => path switch
        {
            $"{Dir}/token" => token,
            $"{Dir}/ca.crt" => ca,
            _ => throw new FileNotFoundException(path),
        };

    [Fact]
    public void In_cluster_config_reads_the_kubelet_mount_and_the_env()
    {
        var config = InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "10.96.0.1"), ("KUBERNETES_SERVICE_PORT_HTTPS", "6443")), Files($"{Token}\n", Ca));

        // The trailing newline the kubelet writes must be gone: it would ride into the
        // Authorization header, which is rejected for invalid characters rather than anything
        // resembling "your token has a newline in it".
        Assert.Equal(Token, config.Token);
        Assert.Equal("10.96.0.1", config.Host);
        Assert.Equal(6443, config.Port);
        Assert.Equal(Ca, config.Ca);
    }

    [Fact]
    public void The_port_falls_back_to_the_plain_service_port_and_then_to_443()
    {
        Assert.Equal(8443, InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h"), ("KUBERNETES_SERVICE_PORT", "8443")), Files(Token, Ca)).Port);
        Assert.Equal(443, InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h")), Files(Token, Ca)).Port);
        Assert.Equal(6443, InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h"), ("KUBERNETES_SERVICE_PORT", "8443"), ("KUBERNETES_SERVICE_PORT_HTTPS", "6443")), Files(Token, Ca)).Port);
    }

    [Fact]
    public void No_host_means_no_default_it_refuses_rather_than_guessing_a_cluster()
    {
        var error = Assert.Throws<InClusterConfigException>(() => InClusterConfig.Read(Env(), Files(Token, Ca)));
        Assert.Contains("KUBERNETES_SERVICE_HOST is not set", error.Message);
    }

    [Fact]
    public void An_empty_host_is_refused_as_well_as_a_missing_one()
    {
        var error = Assert.Throws<InClusterConfigException>(() => InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "")), Files(Token, Ca)));
        Assert.Contains("KUBERNETES_SERVICE_HOST is not set", error.Message);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    public void The_port_range_is_inclusive_at_both_ends(string port, int expected)
    {
        Assert.Equal(expected, InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h"), ("KUBERNETES_SERVICE_PORT_HTTPS", port)), Files(Token, Ca)).Port);
    }

    [Fact]
    public void An_empty_CA_is_refused_because_the_fallback_is_the_public_root_store()
    {
        var error = Assert.Throws<InClusterConfigException>(() => InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h")), Files(Token, "   \n")));
        Assert.Contains("refusing to fall back to the public root store", error.Message);
    }

    [Fact]
    public void An_empty_token_is_refused()
    {
        var error = Assert.Throws<InClusterConfigException>(() => InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h")), Files("\n", Ca)));
        Assert.Matches("token .* is empty", error.Message);
    }

    [Theory]
    [InlineData("tcp://10.96.0.1:443")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("")]
    public void A_bad_port_is_refused_rather_than_composed_into_a_URL(string port)
    {
        var error = Assert.Throws<InClusterConfigException>(() => InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h"), ("KUBERNETES_SERVICE_PORT_HTTPS", port)), Files(Token, Ca)));
        Assert.Contains("KUBERNETES_SERVICE_PORT must be an integer", error.Message);
    }

    [Fact]
    public void Unreadable_service_account_files_name_the_automount_setting()
    {
        var error = Assert.Throws<InClusterConfigException>(() => InClusterConfig.Read(Env(("KUBERNETES_SERVICE_HOST", "h")), _ => throw new FileNotFoundException("no such file")));
        Assert.Contains("cannot read the service account at /var/run/secrets/kubernetes.io/serviceaccount: no such file", error.Message);
        Assert.Contains("automountServiceAccountToken: false", error.Message);
    }
}
