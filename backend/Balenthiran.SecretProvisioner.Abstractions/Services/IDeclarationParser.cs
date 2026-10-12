using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;

namespace Balenthiran.SecretProvisioner.Abstractions.Services;

/// <summary>
/// The pure half of the declarations loader: everything decided about what GitHub returned,
/// with no I/O, so every refusal is testable without a network. The HTTP half
/// (<see cref="IDeclarationSource"/>) hands responses in.
/// </summary>
/// <remarks>
/// Every method throws rather than returning less: there is no path that yields fewer
/// declarations than the listing named. A create-only provisioner given four of five creates
/// four Secrets, reports success and leaves one app unprovisioned with nothing saying so.
/// </remarks>
public interface IDeclarationParser
{
    /// <summary>The branch to read. Refuses anything that does not name one literally, and refuses <c>HEAD</c>.</summary>
    string AssertBranch(string? branch);

    /// <summary>
    /// The <c>*.json</c> blobs in a contents listing, in the validator's order: top level only,
    /// ordinal sort. <paramref name="dir"/> and <paramref name="sha"/> only make the messages exact.
    /// </summary>
    IReadOnlyList<IDeclarationBlob> DeclarationBlobs(JsonElement listing, string dir, string sha);

    /// <summary>
    /// The shape this provisioner itself dereferences, deliberately NOT the schema: that lives
    /// in oke-fleet's validator. An unknown key <c>type</c> is accepted here on purpose.
    /// </summary>
    ISecretDeclaration AssertUsable(JsonElement value, string name);

    /// <summary>Parse the fetched files (name is the path, body the text) into the declarations the planner consumes.</summary>
    IReadOnlyList<ISecretDeclaration> ParseDeclarations(IEnumerable<(string Name, string Body)> files);

    /// <summary>Pair the declarations with the commit they were read at.</summary>
    IDeclarationSet Assemble(string owner, string repo, string branch, string sha, IReadOnlyList<ISecretDeclaration> declarations);
}
