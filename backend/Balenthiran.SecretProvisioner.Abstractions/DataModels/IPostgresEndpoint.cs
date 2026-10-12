namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>Where the Postgres server is, as the apps' connection strings will name it.</summary>
public interface IPostgresEndpoint
{
    /// <summary>Required, with no default: a guessed host composes a connection string that is syntactically perfect and resolves to nothing.</summary>
    string Host { get; }

    /// <summary>Absent means 5432.</summary>
    int? Port { get; }
}
