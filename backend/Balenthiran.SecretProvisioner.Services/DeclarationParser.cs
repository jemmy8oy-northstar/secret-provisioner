using System.Text.Encodings.Web;
using System.Text.Json;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services;

/// <summary>
/// Port of the pure half of <c>src/declarations.js</c>. See <see cref="IDeclarationParser"/> for the contract.
/// </summary>
/// <remarks>
/// NOTHING HERE IS A SECRET, AND THAT IS WHY THIS FILE IS SIMPLE. A declaration states intent —
/// namespace, Secret name, key, and how the value should come to exist. It never holds a value,
/// so unlike the observer this class needs no redaction discipline: errors quote URLs and bodies freely.
/// </remarks>
public sealed class DeclarationParser : IDeclarationParser
{
    // `readdirSync(secretsDir).filter((f) => f.endsWith('.json')).sort()` — that is the literal
    // line in `oke-fleet:scripts/validate-secrets.mjs`, and this is the other side of the same
    // seam. It must enumerate EXACTLY what the validator validates: `.json`, top level only,
    // sorted. A file the validator never saw is a file CI never checked, and provisioning from an
    // unchecked declaration is how a typo reaches a `CREATE DATABASE`.
    private const string DeclarationExt = ".json";

    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string AssertBranch(string? branch)
    {
        if (string.IsNullOrEmpty(branch))
        {
            throw new InvalidDeclarationSourceException($"refusing to read declarations from {Quote(branch)} — the branch must be named literally");
        }

        // WHY `dev`, LITERALLY, AND WHY `HEAD` IS REFUSED. `apps-root/fleet-generator.yaml` uses
        // `targetRevision: HEAD`, and in Argo CD `HEAD` means the repository's DEFAULT branch —
        // `main`. So Argo releases from `main` and this provisioner reads `dev`: that one
        // difference is what makes provisioning a PRECONDITION of release. A renamed default branch
        // would silently re-converge on `main` and the only symptom would be an app released before
        // its Secret existed. Naming `dev` literally makes that failure loud.
        if (branch == "HEAD")
        {
            throw new InvalidDeclarationSourceException(
                "refusing to read declarations from `HEAD`. HEAD is a setting meaning \"the default branch\", which here is `main` — the branch Argo CD releases from. "
                + "The provisioner reads `dev` so that provisioning happens BEFORE release; resolving HEAD would silently collapse that ordering.");
        }

        return branch;
    }

    public IReadOnlyList<IDeclarationBlob> DeclarationBlobs(JsonElement listing, string dir, string sha)
    {
        // The contents API answers a MISSING path with `{"message": "Not Found"}` and a single
        // file with an object — both valid JSON, and both of which a lenient filter would turn into
        // "no declarations". An answer that is not a directory listing is not an empty directory.
        if (listing.ValueKind != JsonValueKind.Array)
        {
            var said = listing.ValueKind == JsonValueKind.Object && listing.TryGetProperty("message", out var message)
                ? $" — GitHub said {message.GetRawText()}"
                : string.Empty;
            throw new InvalidDeclarationListingException($"the contents listing for {dir} is not an array{said}. This is not an empty directory; it is a failed read");
        }

        var named = listing.EnumerateArray()
            .Where(entry => StringOf(entry, "name") is { } name && name.EndsWith(DeclarationExt, StringComparison.Ordinal))
            .ToList();

        // A `*.json` entry that is not a plain file is REFUSED, not skipped — and that asymmetry is
        // the point. The contents API also returns `dir`, `symlink` and `submodule`. oke-fleet's
        // validator uses `readFileSync`, which FOLLOWS a symlink, so a symlinked declaration is one
        // this estate's CI has validated and passed. Dropping it here would mean the provisioner
        // quietly ignoring a declaration oke-fleet considers live — the silent under-provisioning
        // this whole module refuses. Anything the two sides would read differently has to stop the run.
        foreach (var entry in named)
        {
            if (StringOf(entry, "type") != "file")
            {
                throw new InvalidDeclarationListingException($"{StringOf(entry, "path")} is a {Quote(StringOf(entry, "type"))}, not a file. oke-fleet's validator reads it with readFileSync and this reads it over HTTPS, so the two would not agree about what it contains");
            }
        }

        var blobs = named
            .Select(entry => (Name: StringOf(entry, "name")!, Path: StringOf(entry, "path"), Url: StringOf(entry, "download_url")))
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .ToList();

        if (blobs.Count == 0)
        {
            // Deliberately an error. A create-only provisioner with nothing to create does no work
            // either way, so refusing costs nothing — while succeeding would make "the ref is
            // wrong", "the directory moved" and "he deleted every declaration" produce one
            // identical clean reconcile.
            throw new InvalidDeclarationListingException($"no {DeclarationExt} declarations under {dir}/ at {sha}. A wrong ref and an empty directory look the same from here, so this is a refusal rather than an empty run");
        }

        var result = new List<IDeclarationBlob>();
        foreach (var (name, listedPath, url) in blobs)
        {
            // A listing entry always has a path; the name is the fallback so a message can still name the file.
            var path = listedPath ?? name;
            if (string.IsNullOrEmpty(url))
            {
                throw new InvalidDeclarationListingException($"{path} has no download URL in the listing — nothing can be fetched for it");
            }

            // The listing was requested at a SHA, so every download_url GitHub returns is pinned to
            // that SHA. If one is not, the listing did not come from the commit we asked for, and the
            // set about to be assembled would span two trees. Checking is one line; the failure it
            // prevents is a declaration set that never existed as a commit.
            if (!url.Contains(sha, StringComparison.Ordinal))
            {
                throw new InvalidDeclarationListingException($"{path}'s download URL is not pinned to {sha} ({url}) — the listing and the files would come from different commits");
            }

            result.Add(new DeclarationBlob { Name = name, Path = path, Url = url });
        }

        return result;
    }

    public ISecretDeclaration AssertUsable(JsonElement value, string name)
    {
        // The shape this provisioner itself dereferences. Deliberately NOT the schema:
        // `oke-fleet:scripts/validate-secrets.mjs` is the schema, it runs in oke-fleet's CI on every
        // pull request into `dev`, and re-implementing it here would give this estate two
        // definitions of a valid declaration that drift apart in silence — with the copy nobody
        // edits being the one that decides what gets created. What is checked is only what would
        // otherwise fail HALF WAY THROUGH a reconcile: a create-only run that has already made
        // three Secrets and then throws on a missing field leaves the cluster in a state nothing planned.
        static InvalidDeclarationException Bad(string name, string why) => new($"{name}: {why}");

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Bad(name, "a declaration must be a JSON object");
        }

        foreach (var field in new[] { "namespace", "secretName" })
        {
            if (string.IsNullOrEmpty(StringOf(value, field)))
            {
                throw Bad(name, $"{field} must be a non-empty string");
            }
        }

        if (!value.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
        {
            throw Bad(name, "keys must be a non-empty array");
        }

        var declared = new List<KeyDeclaration>();
        var i = 0;
        foreach (var entry in keys.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw Bad(name, $"keys[{i}] must be an object");
            }

            if (string.IsNullOrEmpty(StringOf(entry, "key")))
            {
                throw Bad(name, $"keys[{i}].key must be a non-empty string");
            }

            // Deliberately no check against a list of valid types: the planner reports an unknown one.
            if (string.IsNullOrEmpty(StringOf(entry, "type")))
            {
                throw Bad(name, $"keys[{i}].type must be a non-empty string");
            }

            declared.Add(new KeyDeclaration
            {
                Key = StringOf(entry, "key")!,
                Type = StringOf(entry, "type")!,
                Database = StringOf(entry, "database"),
                Role = StringOf(entry, "role"),
                Bytes = entry.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt32(out var n) ? n : null,
            });
            i++;
        }

        return new SecretDeclaration
        {
            App = StringOf(value, "app") ?? string.Empty,
            Namespace = StringOf(value, "namespace")!,
            SecretName = StringOf(value, "secretName")!,
            Keys = declared,
        };
    }

    public IReadOnlyList<ISecretDeclaration> ParseDeclarations(IEnumerable<(string Name, string Body)> files)
    {
        var parsed = new List<ISecretDeclaration>();
        foreach (var (name, body) in files)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException error)
            {
                // raw.githubusercontent answers a missing blob with the plain text `404: Not Found`,
                // which reaches here as a parse error. Quoting the start of the body is what tells
                // those two apart in a log.
                throw new InvalidDeclarationException($"{name}: not JSON ({error.Message}) — the response began {Quote(body[..Math.Min(40, body.Length)])}");
            }

            using (document)
            {
                parsed.Add(AssertUsable(document.RootElement, name));
            }
        }

        return parsed;
    }

    public IDeclarationSet Assemble(string owner, string repo, string branch, string sha, IReadOnlyList<ISecretDeclaration> declarations) => new DeclarationSet
    {
        Owner = owner,
        Repo = repo,
        Branch = branch,
        Sha = sha,
        Declarations = declarations.Select(d => new SecretDeclaration
        {
            App = d.App,
            Namespace = d.Namespace,
            SecretName = d.SecretName,
            Keys = d.Keys.Select(k => new KeyDeclaration { Key = k.Key, Type = k.Type, Database = k.Database, Role = k.Role, Bytes = k.Bytes }).ToList(),
        }).ToList(),
    };

    // A string property, or null when it is absent or not a string — the JS `typeof x === 'string'`.
    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // JSON.stringify for a message: quoted, escaped, and `null` for nothing.
    private static string Quote(string? text) => JsonSerializer.Serialize(text, QuoteOptions);
}
