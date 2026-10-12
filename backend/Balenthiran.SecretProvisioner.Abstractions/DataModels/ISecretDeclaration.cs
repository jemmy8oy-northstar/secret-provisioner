namespace Balenthiran.SecretProvisioner.Abstractions.DataModels;

/// <summary>One <c>oke-fleet/secrets/*.json</c> file: which Secret, in which namespace, and how each key comes to exist. Never a value.</summary>
public interface ISecretDeclaration
{
    string App { get; }

    string Namespace { get; }

    string SecretName { get; }

    IReadOnlyList<IKeyDeclaration> Keys { get; }
}
