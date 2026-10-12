using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// The Postgres edge. It connects as a non-superuser provisioning role: Postgres 16 restricts
/// <c>CREATEROLE</c> so a role may only alter roles it created itself, so this service cannot
/// touch <c>postgres</c> or any role it did not make.
/// </summary>
/// <remarks>
/// <see cref="CreateRoleAsync"/> must be a plain <c>CREATE ROLE</c>, which Postgres refuses with
/// 42710 if the role exists — never <c>IF NOT EXISTS</c>, never an <c>ALTER ROLE</c>. An error
/// from it can quote the failing statement, password included, so callers redact before they
/// record one.
/// </remarks>
public interface IPostgresClient
{
    /// <summary>Every role and database name, unfiltered (<c>pg_roles.rolname</c>, <c>pg_database.datname</c>).</summary>
    Task<IPostgresCatalogue> ReadCatalogueAsync(CancellationToken cancellationToken = default);

    /// <summary><c>CREATE ROLE {role} LOGIN PASSWORD {password}</c>.</summary>
    Task CreateRoleAsync(string role, string password, CancellationToken cancellationToken = default);

    /// <summary><c>CREATE DATABASE {database} OWNER {owner}</c>.</summary>
    Task CreateDatabaseAsync(string database, string owner, CancellationToken cancellationToken = default);
}
