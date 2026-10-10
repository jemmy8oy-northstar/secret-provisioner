namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The in-cluster connection details are missing or unusable. Every field is required and none is
/// defaulted: a default is a client that silently talks to the wrong cluster, or to none.
/// </summary>
public sealed class InClusterConfigException : AppException
{
    public InClusterConfigException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "in_cluster_config_invalid";
}
