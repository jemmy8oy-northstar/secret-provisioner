namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>One key of a declared Secret. <see cref="Type"/> decides how its value comes to exist: <c>postgres-db</c>, <c>random</c> or <c>external</c>.</summary>
public interface IKeyDeclaration
{
    string Key { get; }

    string Type { get; }

    /// <summary><c>postgres-db</c> only.</summary>
    string? Database { get; }

    /// <summary><c>postgres-db</c> only.</summary>
    string? Role { get; }

    /// <summary><c>random</c> only; absent means 32.</summary>
    int? Bytes { get; }
}
