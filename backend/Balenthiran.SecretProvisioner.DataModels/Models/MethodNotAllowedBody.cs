using System.Text.Json.Serialization;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>
/// The 405 body. Names the methods that DO work on this path, so <c>GET /reconcile</c> and a typo
/// do not produce the same answer and nobody learns that something tried to provision with a GET.
/// </summary>
public sealed class MethodNotAllowedBody
{
    [JsonPropertyOrder(1)]
    public string Error { get; } = "method not allowed";

    [JsonPropertyOrder(2)]
    public required IReadOnlyList<string> Allowed { get; init; }
}
