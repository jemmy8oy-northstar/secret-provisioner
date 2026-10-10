using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>One key a <c>create-secret</c> step writes, and where its value comes from — in prose, never the value.</summary>
public sealed class StepKey : IStepKey
{
    [JsonPropertyOrder(1)]
    public required string Key { get; init; }

    [JsonPropertyOrder(2)]
    public required string Type { get; init; }

    [JsonPropertyOrder(3)]
    public required string Source { get; init; }
}
