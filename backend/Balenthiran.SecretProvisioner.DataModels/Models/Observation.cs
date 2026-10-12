using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>What was actually looked at. A namespace absent from <see cref="Namespaces"/> means "not enumerated", never "empty" — the distinction the planner exists to hold.</summary>
public sealed class Observation : IObservation
{
    /// <summary>Namespaces in which every declared Secret was probed. Null is refused by the planner.</summary>
    [JsonPropertyOrder(1)]
    public List<string>? Namespaces { get; init; }

    /// <summary>Existing Secrets by <c>namespace/name</c>. A null list means "exists, not read", which is all the create-only probe can report. Never a value.</summary>
    [JsonPropertyOrder(2)]
    public Dictionary<string, List<string>?>? Secrets { get; init; }

    /// <summary>Null means the server was not inspected — not that it is empty.</summary>
    [JsonPropertyOrder(3)]
    public PostgresObservation? Postgres { get; init; }

    /// <summary>Namespaces in which a probe failed. They are deliberately NOT in <see cref="Namespaces"/>.</summary>
    [JsonPropertyOrder(4)]
    public List<NamespaceFailure>? Failures { get; init; }

    IReadOnlyList<string>? IObservation.Namespaces => Namespaces;
    // A Dictionary of List is not covariant to the read-only view, so the view is a projection.
    IReadOnlyDictionary<string, IReadOnlyList<string>?>? IObservation.Secrets =>
        Secrets?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>?)kv.Value);
    IPostgresObservation? IObservation.Postgres => Postgres;
    IReadOnlyList<INamespaceFailure>? IObservation.Failures => Failures;
}
