namespace Balenthiran.SecretProvisioner.Abstractions.Exceptions;

/// <summary>
/// The Postgres host is missing or the port is not a port. Refused before anything is created: a
/// wrong endpoint composes a connection string that is syntactically perfect and resolves to
/// nothing, and create-only means the Secret holding it can never be rewritten.
/// </summary>
public sealed class InvalidPostgresEndpointException : AppException
{
    public InvalidPostgresEndpointException(string message)
        : base(message)
    {
    }

    public override string ErrorCode => "invalid_postgres_endpoint";
}
