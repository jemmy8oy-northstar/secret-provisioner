using System.Text.Json.Serialization;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>
/// The 500 body. Built from the route and a fixed string, never from the exception: a Postgres
/// error quotes the statement that failed and that statement contains a password.
/// </summary>
public sealed class HandlerFailedBody
{
    [JsonPropertyOrder(1)]
    public required string Error { get; init; }

    [JsonPropertyOrder(2)]
    public string Detail { get; } = "see the pod log";
}
