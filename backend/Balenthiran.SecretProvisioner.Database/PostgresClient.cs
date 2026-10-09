using System.Text.RegularExpressions;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;
using Npgsql;

namespace Balenthiran.SecretProvisioner.Database;

/// <summary>
/// The Postgres edge, over Npgsql. See <see cref="IPostgresClient"/> for the contract.
/// </summary>
/// <remarks>
/// DDL CANNOT TAKE BIND PARAMETERS. <c>CREATE ROLE</c>, <c>CREATE DATABASE</c> and <c>GRANT</c>
/// name identifiers, and <c>PASSWORD</c> takes a literal, so every statement here is built as
/// text. The guards below are therefore the injection defence, and they are deliberately far
/// tighter than Postgres: an identifier must match oke-fleet's validator
/// (<c>[a-z_][a-z0-9_]*</c>, at most 63 bytes) and a password must be lowercase hex, which is
/// all the executor ever generates. Anything else is refused, never quoted or escaped. The
/// validator already ran in oke-fleet's CI; checking again here is because this is the line
/// that executes.
/// <para>
/// WHY <see cref="CreateRoleAsync"/> ALSO GRANTS. This service connects as a non-superuser with
/// <c>CREATEROLE</c> and <c>CREATEDB</c>. Postgres 16+ (the estate runs <c>postgres:17</c>)
/// requires that "to create a database owned by another role, you must be able to SET ROLE to
/// that role", and a <c>CREATEROLE</c> user gets only <c>ADMIN OPTION</c> on a role it creates
/// unless <c>createrole_self_grant</c> is set, which defaults to empty. So
/// <c>CREATE DATABASE x OWNER x</c> would fail straight after <c>CREATE ROLE x</c> succeeded,
/// leaving a role with a password nobody kept. The documented remedy is the explicit
/// <c>GRANT x TO CURRENT_USER WITH SET TRUE, INHERIT FALSE</c>, which <c>ADMIN OPTION</c> always
/// permits. <c>INHERIT FALSE</c> means this service never silently holds an app's privileges; it
/// can only act as the role when it explicitly sets it, which it never does.
/// ⚠️ Taken from the Postgres 17 documentation, NOT from a run against a real server: there is
/// no Postgres in the pod this was written in. The statement text is under test here; whether
/// the server accepts it is not, until the service first runs in the cluster.
/// </para>
/// </remarks>
public sealed partial class PostgresClient : IPostgresClient
{
    private readonly NpgsqlDataSource dataSource;

    public PostgresClient(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public async Task<IPostgresCatalogue> ReadCatalogueAsync(CancellationToken cancellationToken = default) => new PostgresCatalogue
    {
        RoleNames = await ReadColumnAsync("SELECT rolname FROM pg_roles", cancellationToken),
        DatabaseNames = await ReadColumnAsync("SELECT datname FROM pg_database", cancellationToken),
    };

    public async Task CreateRoleAsync(string role, string password, CancellationToken cancellationToken = default)
    {
        // One transaction: if the GRANT is refused, the role is not left behind half-usable.
        // CREATE ROLE is transactional in Postgres; CREATE DATABASE is not, which is why it is
        // a separate call and comes second.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var statement in CreateRoleStatements(role, password))
        {
            await using var command = new NpgsqlCommand(statement, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CreateDatabaseAsync(string database, string owner, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CreateDatabaseStatement(database, owner), connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The statements <see cref="CreateRoleAsync"/> runs, in order. A plain <c>CREATE ROLE</c>, never
    /// <c>IF NOT EXISTS</c>: an existing role must be refused by the server (42710), because create-only
    /// forbids giving it a password.
    /// </summary>
    public IReadOnlyList<string> CreateRoleStatements(string role, string password)
    {
        Identifier(role, "role");
        if (!HexPassword().IsMatch(password))
        {
            // The value itself is never put in this message.
            throw new ArgumentException($"refusing to create role {role}: the password is not lowercase hex, and this client never quotes a password it did not generate", nameof(password));
        }

        return
        [
            $"CREATE ROLE {role} LOGIN PASSWORD '{password}'",
            $"GRANT {role} TO CURRENT_USER WITH SET TRUE, INHERIT FALSE",
        ];
    }

    /// <summary>The statement <see cref="CreateDatabaseAsync"/> runs.</summary>
    public string CreateDatabaseStatement(string database, string owner)
    {
        Identifier(database, "database");
        Identifier(owner, "owner");
        return $"CREATE DATABASE {database} OWNER {owner}";
    }

    private static void Identifier(string value, string what)
    {
        if (!PgIdentifier().IsMatch(value) || System.Text.Encoding.UTF8.GetByteCount(value) > 63)
        {
            throw new ArgumentException($"refusing to use {what} {System.Text.Json.JsonSerializer.Serialize(value)} in DDL: it must be a lowercase Postgres identifier — [a-z_][a-z0-9_]*, max 63 bytes", what);
        }
    }

    private async Task<List<string?>> ReadColumnAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string?>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return values;
    }

    // `\z`, not `$`: in .NET `$` also matches before a final newline, so "abc\n" would pass —
    // unlike the JavaScript validator these were ported from, where `$` means the end.
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*\z")]
    private static partial Regex PgIdentifier();

    [GeneratedRegex(@"^[0-9a-f]+\z")]
    private static partial Regex HexPassword();
}
