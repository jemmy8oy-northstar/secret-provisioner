using System.Text.Json;
using System.Text.Json.Nodes;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Services;

namespace Balenthiran.SecretProvisioner.Tests;

/// <summary>Port of the pure cases of <c>src/declarations.test.js</c>, case for case.</summary>
public class DeclarationParserTests
{
    // `Fixtures/Declarations/contents-listing.json` is GitHub's ACTUAL answer, saved verbatim, not
    // a drawing of one, captured at oke-fleet `dev` = 1fa0825…, which held README.md and three
    // declarations. This class's whole job is to read one specific API's response, so a
    // hand-written listing would be testing my memory of that response — and it would go on passing
    // after the response changed, which is the one thing a fixture on a seam must not do.
    internal const string ListingSha = "1fa08255183319b82ea228db097534557f6564df";
    private const string Dir = "secrets";

    private readonly DeclarationParser parser = new();

    internal static JsonArray ListingNode() => JsonNode.Parse(File.ReadAllText(Json.Fixture("Declarations", "contents-listing.json")))!.AsArray();

    private static JsonElement Element(JsonNode? node) => JsonDocument.Parse(node?.ToJsonString() ?? "null").RootElement.Clone();

    private static JsonNode Entry(string name, string type, string? downloadUrl) => new JsonObject
    {
        ["name"] = name,
        ["path"] = $"secrets/{name}",
        ["type"] = type,
        ["download_url"] = downloadUrl,
    };

    private static string Pinned(string name) => $"https://raw.githubusercontent.com/x/y/{ListingSha}/secrets/{name}";

    private IEnumerable<string> Names(JsonNode listing) => parser.DeclarationBlobs(Element(listing), Dir, ListingSha).Select(b => b.Name);

    private static readonly string[] ThreeDeclarations = ["around-the-world.json", "balenthiran.json", "macro-metrics.json"];

    // ── the branch is named literally ───────────────────────────────────────

    [Fact]
    public void HEAD_is_refused_rather_than_resolved()
    {
        // Argo CD's `targetRevision: HEAD` means "the default branch" = `main`. If this ever
        // resolved HEAD it would read the same branch Argo releases from, the ordering that makes
        // provisioning a precondition would be gone, and nothing would look wrong until an app
        // started without its Secret.
        var error = Assert.Throws<InvalidDeclarationSourceException>(() => parser.AssertBranch("HEAD"));
        Assert.Contains("refusing to read declarations from `HEAD`", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_branch_is_refused_so_a_missing_config_cannot_become_a_default(string? bad)
    {
        var error = Assert.Throws<InvalidDeclarationSourceException>(() => parser.AssertBranch(bad));
        Assert.Contains("the branch must be named literally", error.Message);
    }

    [Fact]
    public void A_literal_branch_is_returned_as_it_was_given() => Assert.Equal("dev", parser.AssertBranch("dev"));

    [Fact]
    public void The_refusals_have_distinct_error_codes()
    {
        var codes = new AppException[]
        {
            new InvalidDeclarationSourceException("x"),
            new InvalidDeclarationListingException("x"),
            new InvalidDeclarationException("x"),
            new DeclarationFetchException("x"),
        }.Select(e => e.ErrorCode);
        Assert.Equal(4, codes.Distinct().Count());
    }

    // ── the listing ─────────────────────────────────────────────────────────

    [Fact]
    public void The_three_declarations_are_found_and_README_is_not() => Assert.Equal(ThreeDeclarations, Names(ListingNode()));

    [Fact]
    public void Blobs_carry_the_listings_name_path_and_download_url()
    {
        var blob = parser.DeclarationBlobs(Element(ListingNode()), Dir, ListingSha)[1];
        Assert.Equal("balenthiran.json", blob.Name);
        Assert.Equal("secrets/balenthiran.json", blob.Path);
        Assert.Equal($"https://raw.githubusercontent.com/jemmy8oy-northstar/oke-fleet/{ListingSha}/secrets/balenthiran.json", blob.Url);
    }

    [Fact]
    public void README_is_excluded_by_EXTENSION_not_by_name()
    {
        // Excluding the literal string "README.md" passes this suite today and breaks the first
        // time a NOTES.md or a CODEOWNERS appears beside the declarations.
        var listing = ListingNode();
        listing.Add(Entry("NOTES.md", "file", Pinned("NOTES.md")));
        Assert.Equal(ThreeDeclarations, Names(listing));
    }

    [Fact]
    public void A_directory_beside_the_declarations_is_simply_not_one_of_them()
    {
        // oke-fleet's validator is `readdirSync(secretsDir).filter(f => f.endsWith('.json'))` — top
        // level, by extension. A directory named `archive` is not a `.json` and neither side looks inside it.
        var listing = ListingNode();
        listing.Add(Entry("archive", "dir", null));
        Assert.Equal(3, Names(listing).Count());
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("submodule")]
    [InlineData("dir")]
    public void A_json_entry_that_is_NOT_a_plain_file_stops_the_run_and_is_not_silently_skipped(string type)
    {
        // The asymmetry matters. oke-fleet's validator reads with `readFileSync`, which follows a
        // symlink, so a symlinked declaration is one this estate's CI has validated and passed.
        // Skipping it here would mean provisioning fewer apps than oke-fleet believes are declared,
        // with nothing saying so — the silent under-provisioning the whole module refuses.
        var listing = ListingNode();
        listing.Add(Entry("link.json", type, Pinned("link.json")));
        var error = Assert.Throws<InvalidDeclarationListingException>(() => Names(listing).ToList());
        Assert.Contains($"secrets/link.json is a \"{type}\"", error.Message);
    }

    [Fact]
    public void The_order_is_the_validators_order_whatever_order_GitHub_returns()
    {
        var shuffled = new JsonArray([.. ListingNode().Reverse().Select(n => n!.DeepClone())]);
        Assert.Equal(ThreeDeclarations, Names(shuffled));
    }

    [Fact]
    public void The_sort_is_ordinal_so_upper_case_precedes_lower_case()
    {
        // The validator's `.sort()` compares UTF-16 code units. A culture-aware comparison would
        // put `a.json` before `B.json`, and the two sides would disagree about the order.
        var listing = new JsonArray(Entry("a.json", "file", Pinned("a.json")), Entry("B.json", "file", Pinned("B.json")));
        Assert.Equal(["B.json", "a.json"], Names(listing));
    }

    [Fact]
    public void Only_the_top_level_is_enumerated_so_a_nested_path_in_the_name_is_not_special()
    {
        // The listing is of one directory; the extension alone decides, and `.JSON` is not `.json`.
        var listing = ListingNode();
        listing.Add(Entry("SHOUT.JSON", "file", Pinned("SHOUT.JSON")));
        Assert.Equal(ThreeDeclarations, Names(listing));
    }

    [Fact]
    public void A_listing_that_is_not_an_array_is_a_FAILED_read_never_an_empty_directory()
    {
        // The contents API answers a missing path with `{"message":"Not Found"}` — valid JSON, and a
        // lenient filter would turn it into zero declarations and a clean reconcile that provisioned nothing.
        var notFound = Element(new JsonObject { ["message"] = "Not Found" });
        var error = Assert.Throws<InvalidDeclarationListingException>(() => parser.DeclarationBlobs(notFound, Dir, ListingSha));
        Assert.Contains("not an array", error.Message);
        Assert.Contains("\"Not Found\"", error.Message);

        var nothing = Assert.Throws<InvalidDeclarationListingException>(() => parser.DeclarationBlobs(Element(null), Dir, ListingSha));
        Assert.Contains("not an array", nothing.Message);
    }

    [Fact]
    public void Zero_declarations_is_a_refusal_because_a_wrong_ref_and_an_empty_directory_look_identical_from_here()
    {
        var onlyReadme = new JsonArray(ListingNode()[0]!.DeepClone());
        Assert.Contains(".json declarations", Assert.Throws<InvalidDeclarationListingException>(() => Names(onlyReadme).ToList()).Message);
        Assert.Contains(".json declarations", Assert.Throws<InvalidDeclarationListingException>(() => Names(new JsonArray()).ToList()).Message);
    }

    [Fact]
    public void A_download_URL_not_pinned_to_the_resolved_commit_is_refused()
    {
        // This is what keeps the read atomic. A `download_url` on the branch rather than the SHA
        // means a push landing mid-read assembles a set of declarations that never existed as a
        // commit — and every file would still parse.
        var drifted = ListingNode();
        foreach (var entry in drifted)
        {
            entry!["download_url"] = entry["download_url"]!.GetValue<string>().Replace(ListingSha, "dev");
        }

        Assert.Contains("not pinned to", Assert.Throws<InvalidDeclarationListingException>(() => Names(drifted).ToList()).Message);
    }

    [Fact]
    public void A_file_with_no_download_URL_is_refused_rather_than_skipped()
    {
        var broken = ListingNode();
        broken.First(e => e!["name"]!.GetValue<string>() == "balenthiran.json")!["download_url"] = null;
        Assert.Contains("no download URL", Assert.Throws<InvalidDeclarationListingException>(() => Names(broken).ToList()).Message);
    }

    // ── the shape ───────────────────────────────────────────────────────────

    private static JsonObject Usable() => new()
    {
        ["app"] = "a",
        ["namespace"] = "balenthiran",
        ["secretName"] = "a-secrets",
        ["keys"] = new JsonArray(new JsonObject { ["key"] = "K", ["type"] = "random" }),
    };

    private InvalidDeclarationException Refused(JsonNode? declaration, string name = "a.json") =>
        Assert.Throws<InvalidDeclarationException>(() => parser.AssertUsable(Element(declaration), name));

    [Fact]
    public void The_usable_shape_is_accepted()
    {
        var declaration = parser.AssertUsable(Element(Usable()), "a.json");
        Assert.Equal(("a", "balenthiran", "a-secrets"), (declaration.App, declaration.Namespace, declaration.SecretName));
        Assert.Equal(("K", "random"), (declaration.Keys[0].Key, declaration.Keys[0].Type));
    }

    [Fact]
    public void The_optional_fields_of_a_key_survive()
    {
        var declaration = Usable();
        declaration["keys"] = new JsonArray(new JsonObject { ["key"] = "K", ["type"] = "postgres-db", ["database"] = "d", ["role"] = "r", ["bytes"] = 16 });
        var key = parser.AssertUsable(Element(declaration), "a.json").Keys[0];
        Assert.Equal(("d", "r", 16), (key.Database, key.Role, key.Bytes));
    }

    [Fact]
    public void Every_refusal_names_the_file_it_came_from()
    {
        // A reconcile reads several files and the message is all anyone gets — "keys must be a
        // non-empty array" without a filename is a hunt through a public repo for which of three it meant.
        var declaration = Usable();
        declaration["keys"] = new JsonArray();
        Assert.StartsWith("macro-metrics.json: ", Refused(declaration, "macro-metrics.json").Message);
    }

    [Fact]
    public void An_array_is_not_a_declaration_even_though_it_is_an_object()
    {
        Assert.Contains("must be a JSON object", Refused(new JsonArray(Usable())).Message);
        Assert.Contains("must be a JSON object", Refused(null).Message);
    }

    [Fact]
    public void A_missing_namespace_or_secretName_is_refused_because_both_are_addresses_not_defaults()
    {
        var emptyNamespace = Usable();
        emptyNamespace["namespace"] = "";
        Assert.Contains("namespace", Refused(emptyNamespace).Message);

        var noSecretName = Usable();
        noSecretName.Remove("secretName");
        Assert.Contains("secretName", Refused(noSecretName).Message);
    }

    [Fact]
    public void Keys_that_is_not_an_array_is_refused_as_well_as_one_that_is_empty()
    {
        var notAnArray = Usable();
        notAnArray["keys"] = "K";
        Assert.Contains("keys must be a non-empty array", Refused(notAnArray).Message);

        var missing = Usable();
        missing.Remove("keys");
        Assert.Contains("keys must be a non-empty array", Refused(missing).Message);
    }

    [Fact]
    public void A_key_entry_missing_key_or_type_is_refused_and_the_index_is_named()
    {
        static JsonNode TwoKeys(JsonNode second)
        {
            var declaration = Usable();
            declaration["keys"] = new JsonArray(new JsonObject { ["key"] = "K", ["type"] = "random" }, second);
            return declaration;
        }

        Assert.Contains("keys[1].key", Refused(TwoKeys(new JsonObject { ["type"] = "random" })).Message);
        Assert.Contains("keys[1].type", Refused(TwoKeys(new JsonObject { ["key"] = "J" })).Message);
        Assert.Contains("keys[1] must be an object", Refused(TwoKeys(JsonValue.Create("J")!)).Message);
    }

    [Fact]
    public void An_UNKNOWN_type_is_accepted_here_because_the_schema_lives_in_oke_fleet_and_is_not_copied()
    {
        // Deliberate. `oke-fleet:scripts/validate-secrets.mjs` owns the list of valid types and runs
        // in that repo's CI on every pull request into `dev`. Re-implementing it here would give the
        // estate two definitions that drift apart in silence, with the copy nobody edits deciding
        // what gets created. The planner reports a step whose type it does not know.
        var declaration = Usable();
        declaration["keys"] = new JsonArray(new JsonObject { ["key"] = "K", ["type"] = "not-a-real-type" });
        Assert.Equal("not-a-real-type", parser.AssertUsable(Element(declaration), "a.json").Keys[0].Type);
    }

    [Fact]
    public void A_non_JSON_body_names_what_it_actually_got()
    {
        // raw.githubusercontent answers a missing blob with the plain text `404: Not Found`, which
        // arrives here as a parse error. Quoting the start of the body is what separates "the file
        // is corrupt" from "the file is gone".
        var error = Assert.Throws<InvalidDeclarationException>(() => parser.ParseDeclarations([("secrets/a.json", "404: Not Found")]));
        Assert.StartsWith("secrets/a.json: not JSON (", error.Message);
        Assert.Contains("\"404: Not Found\"", error.Message);
    }

    [Fact]
    public void Only_the_start_of_a_long_non_JSON_body_is_quoted()
    {
        var error = Assert.Throws<InvalidDeclarationException>(() => parser.ParseDeclarations([("a.json", new string('x', 100))]));
        Assert.Contains($"\"{new string('x', 40)}\"", error.Message);
        Assert.DoesNotContain(new string('x', 41), error.Message);
    }

    [Fact]
    public void Assemble_pairs_the_declarations_with_the_commit_they_were_read_at()
    {
        var declarations = parser.ParseDeclarations([("a.json", Usable().ToJsonString())]);
        var set = parser.Assemble("o", "r", "dev", ListingSha, declarations);
        Assert.Equal(("o", "r", "dev", ListingSha), (set.Owner, set.Repo, set.Branch, set.Sha));
        Assert.Equal("a-secrets", Assert.Single(set.Declarations).SecretName);
    }
}
