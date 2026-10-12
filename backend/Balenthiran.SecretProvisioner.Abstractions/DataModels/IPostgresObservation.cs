namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>Roles and databases that belong to the estate — system ones filtered out.</summary>
public interface IPostgresObservation
{
    IReadOnlyList<string>? Roles { get; }

    IReadOnlyList<string>? Databases { get; }
}
