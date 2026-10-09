using System.Text.Json.Serialization;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>The 400 body for a request that carries a body to an endpoint that takes none.</summary>
public sealed class BodyRefusedBody
{
    [JsonPropertyOrder(1)]
    public string Error { get; } = "this endpoint takes no body";

    [JsonPropertyOrder(2)]
    public string Why { get; } =
        "the declarations in oke-fleet are the only input; a request body could only be an attempt to pass one another way";
}
