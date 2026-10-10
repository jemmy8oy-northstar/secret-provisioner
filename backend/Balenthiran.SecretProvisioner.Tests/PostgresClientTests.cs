using Balenthiran.SecretProvisioner.Database;
using Npgsql;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>
/// The statement text and its guards. Nothing here reaches a server — there is none in the pod —
/// so whether Postgres 17 accepts these statements is proven only when the service first runs.
/// </summary>
public class PostgresClientTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef";

    // Never opened: building statements needs no connection.
    private static PostgresClient Client() => new(NpgsqlDataSource.Create("Host=unused"));

    [Fact]
    public void Creating_a_role_is_a_plain_create_then_a_set_only_grant_to_itself()
    {
        Assert.Equal(
            [
                $"CREATE ROLE aroundtheworld LOGIN PASSWORD '{Hex}'",
                "GRANT aroundtheworld TO CURRENT_USER WITH SET TRUE, INHERIT FALSE",
            ],
            Client().CreateRoleStatements("aroundtheworld", Hex));
    }

    [Fact]
    public void Never_if_not_exists_and_never_alter()
    {
        // An existing role must be refused by the server: giving it a password is what create-only forbids.
        var sql = string.Join("\n", Client().CreateRoleStatements("r", Hex));
        Assert.DoesNotContain("IF NOT EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Creating_a_database_names_its_owner() =>
        Assert.Equal("CREATE DATABASE the_db OWNER the_role", Client().CreateDatabaseStatement("the_db", "the_role"));

    [Theory]
    [InlineData("Robert")]
    [InlineData("x; DROP ROLE postgres")]
    [InlineData("x\n")]
    [InlineData("1abc")]
    [InlineData("")]
    [InlineData("a-b")]
    [InlineData("\"quoted\"")]
    public void An_identifier_the_validator_would_reject_is_refused_everywhere(string bad)
    {
        Assert.Throws<ArgumentException>(() => Client().CreateRoleStatements(bad, Hex));
        Assert.Throws<ArgumentException>(() => Client().CreateDatabaseStatement(bad, "ok"));
        Assert.Throws<ArgumentException>(() => Client().CreateDatabaseStatement("ok", bad));
    }

    [Fact]
    public void An_identifier_over_63_bytes_is_refused()
    {
        Client().CreateDatabaseStatement(new string('a', 63), "ok");
        Assert.Throws<ArgumentException>(() => Client().CreateDatabaseStatement(new string('a', 64), "ok"));
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("abc'; DROP ROLE postgres; --")]
    [InlineData("abc\n")]
    [InlineData("")]
    [InlineData("q+/=")]
    public void A_password_that_is_not_lowercase_hex_is_refused_and_not_echoed(string bad)
    {
        var refusal = Assert.Throws<ArgumentException>(() => Client().CreateRoleStatements("r", bad));
        if (bad.Length > 0)
        {
            Assert.DoesNotContain(bad, refusal.Message);
        }
    }
}
