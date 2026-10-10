namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>What was actually looked at. A namespace absent from <see cref="Namespaces"/> means "not enumerated", never "empty" — the distinction the planner exists to hold.</summary>
public interface IObservation
{
    /// <summary>Namespaces whose Secrets were listed. Null is refused by the planner.</summary>
    IReadOnlyList<string>? Namespaces { get; }

    /// <summary>Key names per existing Secret, by <c>namespace/name</c>. Never a value.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>?>? Secrets { get; }

    /// <summary>Null means the server was not inspected — not that it is empty.</summary>
    IPostgresObservation? Postgres { get; }

    /// <summary>Namespaces whose listing failed. They are deliberately NOT in <see cref="Namespaces"/>.</summary>
    IReadOnlyList<INamespaceFailure>? Failures { get; }
}
