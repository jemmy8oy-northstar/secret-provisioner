using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>The step that stopped the run, and why.</summary>
public sealed class FailedStep : IFailedStep
{
    [JsonPropertyOrder(1)]
    public required ExecutedStep Step { get; init; }

    /// <summary>Already redacted: an error can quote the failing statement, password included.</summary>
    [JsonPropertyOrder(2)]
    public required string Error { get; init; }

    IExecutedStep IFailedStep.Step => Step;
}
