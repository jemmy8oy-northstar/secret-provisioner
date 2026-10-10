using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>What a provisioner would do, and everything it will not, sorted into the bucket that says why.</summary>
public sealed class Plan : IPlan
{
    /// <summary>Ordered and executable-shaped: Postgres DDL before the writes that name it.</summary>
    [JsonPropertyOrder(1)]
    public required List<PlanStep> Steps { get; init; }

    /// <summary>A human must act; the rules forbid the provisioner from doing so.</summary>
    [JsonPropertyOrder(2)]
    public required List<PlanEntry> Blocked { get; init; }

    /// <summary>Undecidable, because the observation did not cover it.</summary>
    [JsonPropertyOrder(3)]
    public required List<PlanEntry> Unknown { get; init; }

    /// <summary>Present and inconsistent — reported, never acted on.</summary>
    [JsonPropertyOrder(4)]
    public required List<PlanEntry> Drift { get; init; }

    /// <summary>Keys in a declared Secret that no declaration claims. Never deleted.</summary>
    [JsonPropertyOrder(5)]
    public required List<PlanEntry> Unmanaged { get; init; }

    /// <summary>Already as declared.</summary>
    [JsonPropertyOrder(6)]
    public required List<PlanEntry> Noop { get; init; }

    [JsonPropertyOrder(7)]
    public required PlanSummary Summary { get; init; }

    IReadOnlyList<IPlanStep> IPlan.Steps => Steps;
    IReadOnlyList<IPlanEntry> IPlan.Blocked => Blocked;
    IReadOnlyList<IPlanEntry> IPlan.Unknown => Unknown;
    IReadOnlyList<IPlanEntry> IPlan.Drift => Drift;
    IReadOnlyList<IPlanEntry> IPlan.Unmanaged => Unmanaged;
    IReadOnlyList<IPlanEntry> IPlan.Noop => Noop;
    IPlanSummary IPlan.Summary => Summary;
}
