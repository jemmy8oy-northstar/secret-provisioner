namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>What was actually looked at. A namespace absent from <see cref="Namespaces"/> means "not enumerated", never "empty" — the distinction the planner exists to hold.</summary>
public interface IObservation
{
    /// <summary>Namespaces in which every declared Secret was probed. Null is refused by the planner.</summary>
    IReadOnlyList<string>? Namespaces { get; }

    /// <summary>Existing Secrets by <c>namespace/name</c>. A null list means "exists, not read", which is all the create-only probe can report. Never a value.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>?>? Secrets { get; }

    /// <summary>Null means the server was not inspected — not that it is empty.</summary>
    IPostgresObservation? Postgres { get; }

    /// <summary>Namespaces in which a probe failed. They are deliberately NOT in <see cref="Namespaces"/>.</summary>
    IReadOnlyList<INamespaceFailure>? Failures { get; }
}
