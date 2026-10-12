using System.Text.Json.Serialization;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>
/// The 409 body. Not a queue: two reconciles racing to create the same objects produce a report
/// full of "already exists" that reads like a broken cluster, and the run is on demand with a
/// human watching — so the honest answer is "one is already going", not a silent wait.
/// </summary>
public sealed class ReconcileBusyBody
{
    [JsonPropertyOrder(1)]
    public string Error { get; } = "a reconcile is already running";

    [JsonPropertyOrder(2)]
    public string Retry { get; } = "when it finishes";
}
