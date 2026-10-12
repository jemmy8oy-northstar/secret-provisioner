namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>One key a <c>create-secret</c> step writes, and where its value comes from — in prose, never the value.</summary>
public interface IStepKey
{
    string Key { get; }

    string Type { get; }

    string Source { get; }
}
