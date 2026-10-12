namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>Every role and database name the server holds, unfiltered — the observer decides which are ours.</summary>
public interface IPostgresCatalogue
{
    IReadOnlyList<string?> RoleNames { get; }

    IReadOnlyList<string?> DatabaseNames { get; }
}
