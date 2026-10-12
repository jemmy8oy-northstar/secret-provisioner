using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>
/// What a plan step becomes in the report: its identity and its kind, and never a value. Built
/// field by field, never by copying the step, so a field added to a step later is not reported
/// without anyone deciding it should be.
/// </summary>
public sealed class ExecutedStep : IExecutedStep
{
    [JsonPropertyOrder(1)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(2)]
    public required string Detail { get; init; }

    /// <summary><c>create-role</c> only.</summary>
    [JsonPropertyOrder(3)]
    public string? Role { get; init; }

    /// <summary><c>create-database</c> only.</summary>
    [JsonPropertyOrder(4)]
    public string? Database { get; init; }

    /// <summary><c>create-database</c> only: the role that owns it.</summary>
    [JsonPropertyOrder(5)]
    public string? Owner { get; init; }

    /// <summary><c>create-secret</c> only.</summary>
    [JsonPropertyOrder(6)]
    public string? Namespace { get; init; }

    /// <summary><c>create-secret</c> only.</summary>
    [JsonPropertyOrder(7)]
    public string? SecretName { get; init; }

    /// <summary><c>create-secret</c> only: the key NAMES, never the entries that carry values.</summary>
    [JsonPropertyOrder(8)]
    public List<string>? Keys { get; init; }

    IReadOnlyList<string>? IExecutedStep.Keys => Keys;
}
