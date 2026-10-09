using System.Text.Json.Serialization;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.DataModels.Models;

/// <summary>
/// One response from the service. The plan and report are held by their INTERFACE type on
/// purpose: System.Text.Json serialises a property by its declared type, so what reaches the wire
/// is exactly what the interface exposes, whatever the concrete model behind it carries.
/// </summary>
public sealed class ProvisionerView : IProvisionerView
{
    [JsonPropertyOrder(1)]
    public required DeclarationRef Source { get; init; }

    [JsonPropertyOrder(2)]
    public required IObservation Observation { get; init; }

    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IPlan? Plan { get; init; }

    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IExecutionReport? Report { get; init; }

    IDeclarationRef IProvisionerView.Source => Source;
}
