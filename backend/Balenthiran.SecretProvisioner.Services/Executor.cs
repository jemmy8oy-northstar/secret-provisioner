using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Balenthiran.SecretProvisioner.Abstractions.DataModels;
using Balenthiran.SecretProvisioner.Abstractions.Exceptions;
using Balenthiran.SecretProvisioner.Abstractions.Services;
using Balenthiran.SecretProvisioner.DataModels.Models;

namespace Balenthiran.SecretProvisioner.Services;

/// <summary>
/// Port of the JavaScript executor (<c>src/execute.js</c>). See <see cref="IExecutor"/> for the
/// rules. The Kubernetes and Postgres clients are injected, so everything decided here — the
/// ordering, the create-only guarantees, the value generation, the composition of a connection
/// string, and above all what does and does not reach the report — is decided by a test rather
/// than by a live cluster.
/// </summary>
/// <remarks>
/// THE PLAN SAYS WHAT. THE DECLARATIONS SAY WITH WHAT. A step carries the decision and its
/// identity — "patch balenthiran/balenthiran-secrets#DATABASE_URL" — but not the parameters,
/// because <c>Source</c> is prose written for a human ("generate 32 random bytes, hex-encoded").
/// Deriving a cryptographic parameter by parsing an English sentence is not a thing to do, so
/// <c>bytes</c>, <c>role</c> and <c>database</c> are resolved from the declaration the step names.
/// A step with no declaration, or a declaration of a different type, stops the run rather than
/// being reconciled: that disagreement means the plan and the declarations came from different
/// reads and neither is now trustworthy.
///
/// NO VALUE IS EVER REPORTED, AND THAT IS CHECKED RATHER THAN INTENDED. Everything this class
/// generates is a secret, and the difference between reporting a key name and reporting a key's
/// value is one property access. Every error message is passed through <see cref="RedactValues"/>
/// (a Postgres error can quote the failing statement, and that statement contains a password), and
/// <see cref="AssertNoGeneratedValues"/> then walks the finished report. The first is the
/// mechanism; the second is the proof, and it covers reports produced by code not yet written.
///
/// CREATE-ONLY IS ENFORCED BY THE SERVER, NOT BY A CHECK HERE. The plan was made against an
/// observation taken some moments earlier, and a Secret can appear in between. A check-then-act
/// here would be a race; letting the server refuse a duplicate is not. A refusal is a failure, and
/// a failure stops the run.
/// </remarks>
public sealed class Executor : IExecutor
{
    public const int DefaultRandomBytes = 32;
    public const int DefaultPostgresPort = 5432;

    // Npgsql's connection string is keyword=value pairs separated by `;`, so a value containing
    // `;`, `'`, `"` or `=` has to be quoted and escaped. We never do that. Instead every component
    // is required to be free of those characters, and the run stops if one is not.
    //
    // This is why values are generated as HEX and not base64: hex is [0-9a-f], so a generated
    // password can never need quoting. base64 would put `+`, `/` and `=` into passwords, and `=`
    // inside a keyword-value string is where a connection string stops meaning what it looks like.
    // The encoding is a correctness decision, not a cosmetic one.
    private static readonly char[] UnsafeInConnectionString = [';', '\'', '"', '='];

    // Every value this class generates is hex, and the shortest one the schema permits is 16 bytes
    // — 32 hex characters (oke-fleet:scripts/validate-secrets.mjs sets MIN_RANDOM_BYTES = 16).
    // Nothing else that legitimately appears in an error message is a 32-character run of hex. So
    // this catches what exact matching cannot: a value a driver TRUNCATED before putting it in a
    // message, which is no longer equal to anything generated and would sail through.
    private static readonly Regex SecretShaped = new("[0-9a-f]{32,}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IKubernetesClient _kubernetes;
    private readonly IPostgresClient _postgresClient;

    public Executor(IKubernetesClient kubernetes, IPostgresClient postgresClient)
    {
        _kubernetes = kubernetes;
        _postgresClient = postgresClient;
    }

    /// <summary>
    /// Steps run in the order the planner emitted them — Postgres DDL before the Secret writes,
    /// because a connection string cannot be composed before the role it names exists. That
    /// ordering is the planner's decision and is not re-derived here.
    /// </summary>
    /// <remarks>
    /// THE FIRST FAILURE STOPS THE RUN. A failed step means the cluster is not what the plan
    /// assumed, and every later step was chosen on that assumption; the executor cannot tell which
    /// of them depended on it without re-implementing the planner. Stopping costs a re-run, which
    /// is free — <c>/reconcile</c> is on demand with a human watching (James, claude-code-bot#97)
    /// and every step is create-only, so nothing is half-written beyond what the report names.
    ///
    /// <c>blocked</c>, <c>unknown</c>, <c>drift</c>, <c>unmanaged</c> and <c>noop</c> are carried
    /// through untouched. They are not failures and they do not stop anything: they are the things
    /// the planner decided a provisioner must not do, and the executor's job is to report them.
    /// </remarks>
    public async Task<IExecutionReport> ExecuteAsync(IPlan plan, IReadOnlyList<ISecretDeclaration> declarations, IPostgresEndpoint postgres, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(postgres.Host))
        {
            // No default. A guessed host composes a connection string that is syntactically
            // perfect and resolves to nothing, and create-only means the wrong value can never be
            // corrected in place.
            throw new InvalidPostgresEndpointException("config.postgres.host is required — refusing to guess the Postgres host into a connection string that can never be rewritten");
        }

        // Refused here as well as in the composer, so the run stops before anything is created
        // rather than after the roles exist.
        var port = postgres.Port ?? DefaultPostgresPort;
        if (port < 1 || port > 65535)
        {
            throw new InvalidPostgresEndpointException($"config.postgres.port must be an integer from 1 to 65535, got {port}");
        }

        // Run-scoped and in memory only. A password is generated when its role is created and read
        // again when its connection string is composed; it is in no other place, and it is
        // unreachable from the report by construction.
        var run = new Run(IndexDeclarations(declarations), postgres.Host, port);

        var done = new List<ExecutedStep>();
        var skipped = new List<ExecutedStep>();
        FailedStep? failed = null;

        foreach (var step in plan.Steps)
        {
            if (failed is not null)
            {
                skipped.Add(Describe(step));
                continue;
            }

            try
            {
                await RunStepAsync(step, run, cancellationToken);
                done.Add(Describe(step));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed = new FailedStep { Step = Describe(step), Error = RedactValues(error.Message, run.Generated) };
            }
        }

        var report = new ExecutionReport
        {
            Done = done,
            Failed = failed,
            Skipped = skipped,
            Blocked = plan.Blocked,
            Unknown = plan.Unknown,
            Drift = plan.Drift,
            Unmanaged = plan.Unmanaged,
            // `noop` too. It and `unmanaged` are the same kind of thing — an existing key nothing
            // touched — and carrying one but not the other was an omission, not a decision: on a
            // settled estate `noop` is most of the plan, and a report that drops it makes a run
            // that correctly did nothing look like a run that saw nothing.
            Noop = plan.Noop,
            Summary = new ExecutionSummary
            {
                Done = done.Count,
                Failed = failed is null ? 0 : 1,
                Skipped = skipped.Count,
                Generated = run.Generated.Count,
                Blocked = plan.Blocked.Count,
                Unknown = plan.Unknown.Count,
                Drift = plan.Drift.Count,
                Unmanaged = plan.Unmanaged.Count,
                Noop = plan.Noop.Count,
            },
        };

        AssertNoGeneratedValues(report, run.Generated);
        return report;
    }

    /// <summary>
    /// A secret value: <paramref name="bytes"/> random bytes, hex-encoded, lowercase. The
    /// operating system's CSPRNG, unseeded, and that is the whole implementation. This repo is
    /// public (James, claude-code-bot#97), so the generator has to be safe to read — a generator
    /// that is weak when published was weak when it was private.
    /// </summary>
    public static string GenerateHex(int bytes)
    {
        if (bytes < 1)
        {
            throw new ExecutionRefusedException($"refusing to generate a value of {bytes} bytes — a declaration's bytes must be a positive integer");
        }

        return RandomNumberGenerator.GetHexString(bytes * 2, lowercase: true);
    }

    /// <summary>
    /// The Npgsql keyword-value connection string the apps in this estate consume.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: <c>around-the-world-secrets#ConnectionStrings__DefaultConnection</c>
    /// and <c>balenthiran-secrets#DATABASE_URL</c> are both bound to the .NET env var
    /// <c>ConnectionStrings__DefaultConnection</c> (balenthiran.co.uk:helm/values.yaml maps the
    /// key to that name), and both apps use Npgsql.EntityFrameworkCore.PostgreSQL.
    /// <c>DATABASE_URL</c> is a key NAME, not a format: writing a <c>postgres://</c> URL into it
    /// because of what it is called produces a Secret that looks entirely reasonable and an app
    /// that will not start, with a value nobody is allowed to read in order to find out why.
    /// </remarks>
    public static string NpgsqlConnectionString(string? host, int port, string? database, string? username, string? password)
    {
        if (port < 1 || port > 65535)
        {
            throw new ExecutionRefusedException($"connection string: Port must be an integer from 1 to 65535, got {port}");
        }

        var parts = new (string Keyword, string? Value)[]
        {
            ("Host", host),
            ("Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Database", database),
            ("Username", username),
            ("Password", password),
        };

        foreach (var (keyword, value) in parts)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ExecutionRefusedException($"connection string: {keyword} is required");
            }

            if (value.IndexOfAny(UnsafeInConnectionString) >= 0)
            {
                // Deliberately does not name the offending value — this throws on the Password
                // branch too.
                throw new ExecutionRefusedException($"connection string: {keyword} contains one of ; ' \" = and this composer does not quote. Refusing rather than emitting a string that parses as something else");
            }
        }

        return string.Join(';', parts.Select(p => $"{p.Keyword}={p.Value}"));
    }

    /// <summary>Index the declarations by the identity a plan step carries: <c>namespace/secretName#key</c>.</summary>
    public static IReadOnlyDictionary<string, IKeyDeclaration> IndexDeclarations(IReadOnlyList<ISecretDeclaration>? declarations)
    {
        var index = new Dictionary<string, IKeyDeclaration>(StringComparer.Ordinal);
        foreach (var decl in declarations ?? [])
        {
            foreach (var entry in decl.Keys)
            {
                index[$"{decl.Namespace}/{decl.SecretName}#{entry.Key}"] = entry;
            }
        }

        return index;
    }

    /// <summary>
    /// Replace every generated value in a string with a marker. Applied to error messages, because
    /// a Postgres driver error can quote the failing statement and that statement contains a
    /// password.
    /// </summary>
    /// <remarks>
    /// Two passes, and the second is not redundant:
    ///   1. exact, longest first — so a value that contains another is not partially replaced into
    ///      something that no longer matches;
    ///   2. shape — anything hex and long enough to be one of ours, whether or not it still equals
    ///      a value we hold. A message that was truncated, or that quotes a value this run did not
    ///      generate, is redacted too.
    /// Pass 2 alone would be a heuristic; pass 1 alone has a blind spot. Together the blind spot is
    /// a value shorter than 32 hex characters, which this class cannot produce.
    /// </remarks>
    public static string RedactValues(string text, IEnumerable<string> values)
    {
        var output = text;
        foreach (var value in values.OrderByDescending(v => v.Length))
        {
            if (value.Length == 0)
            {
                continue;
            }

            output = output.Replace(value, "<redacted>", StringComparison.Ordinal);
        }

        return SecretShaped.Replace(output, "<redacted>");
    }

    /// <summary>
    /// The property this class exists to hold: nothing it generated is in what it returns. Public
    /// and called directly by its tests, because through <see cref="ExecuteAsync"/> it is
    /// unreachable by construction — and a guard no test can execute is indistinguishable from one
    /// that does not work. It walks the serialised report rather than known fields, so a field
    /// added later by someone who has not read this comment is covered too.
    /// </summary>
    /// <remarks>
    /// ITS SCOPE IS THIS RUN'S OWN RANDOMNESS, AND ONLY THAT. <c>Blocked</c>, <c>Unknown</c>,
    /// <c>Drift</c>, <c>Unmanaged</c>, <c>Noop</c> and every <c>Detail</c> string are the
    /// planner's prose, carried through verbatim, and this method has no way to recognise a secret
    /// embedded in them. That is safe today for a reason worth stating rather than assuming: the
    /// planner is pure over declarations and an observation, the observation is built by the
    /// observer, which drops every value at the earliest point, and the planner writes
    /// <c>&lt;generated&gt;</c> where a password would go. <see cref="RedactValues"/>'s shape pass
    /// is the only thing that would catch a regression there, and it only runs on error messages.
    /// If the planner ever gains a source of real values, this is the comment that was wrong.
    /// </remarks>
    public static void AssertNoGeneratedValues(IExecutionReport report, IReadOnlyCollection<string> values)
    {
        var serialised = JsonSerializer.Serialize(report, report.GetType(), ReportOptions);
        foreach (var value in values)
        {
            if (value.Length > 0 && serialised.Contains(value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"provisioner bug: a generated secret value reached the report. {values.Count} value(s) generated this run; at least one is in the {serialised.Length}-character report that POST /reconcile would return");
            }
        }
    }

    /// <summary>
    /// The declaration entry a write step names — or a refusal. The type is re-checked because the
    /// two artefacts are joined on identity alone: a plan that says <c>random</c> where the
    /// declaration now says <c>external</c> means the declarations changed between planning and
    /// executing, and the safe response to that is to stop, not to pick one.
    /// </summary>
    private static IKeyDeclaration EntryFor(Run run, IPlanStep step, string key, string? type)
    {
        var at = $"{step.Namespace}/{step.SecretName}#{key}";
        if (!run.Index.TryGetValue(at, out var entry))
        {
            throw new ExecutionRefusedException($"refusing to execute: the plan writes {at}, which no declaration claims — the plan and the declarations came from different reads");
        }

        if (entry.Type != type)
        {
            throw new ExecutionRefusedException($"refusing to execute: the plan writes {at} as {type}, the declaration says {entry.Type} — the declarations changed after the plan was made");
        }

        return entry;
    }

    private async Task RunStepAsync(IPlanStep step, Run run, CancellationToken cancellationToken)
    {
        switch (step.Kind)
        {
            case "create-role":
                var role = Required(step.Role, step, "role");
                if (run.Passwords.ContainsKey(role))
                {
                    // Two create-role steps for one role is a plan contradicting itself. Left
                    // alone, the second password overwrites the first in the map while the FIRST is
                    // the one the server was given, so the connection string would be composed from
                    // a password that was never set.
                    throw new ExecutionRefusedException($"refusing to execute: the plan creates role {role} twice, and the connection string would be composed from the second password while the server holds the first");
                }

                var password = GenerateHex(DefaultRandomBytes);
                run.Generated.Add(password);

                // Recorded before the call, not after: if CreateRoleAsync throws having
                // nonetheless created the role, the password must still be redactable out of the
                // error message.
                run.Passwords[role] = password;
                await _postgresClient.CreateRoleAsync(role, password, cancellationToken);
                return;

            case "create-database":
                await _postgresClient.CreateDatabaseAsync(Required(step.Database, step, "database"), Required(step.Role, step, "role"), cancellationToken);
                return;

            case "create-secret":
                var data = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var k in step.Keys ?? [])
                {
                    data[k.Key] = Materialise(step, k.Key, k.Type, run);
                }

                await _kubernetes.CreateSecretAsync(step.Namespace, step.SecretName, data, cancellationToken);
                return;

            case "patch-secret-key":
                var key = Required(step.Key, step, "key");
                await _kubernetes.PatchSecretKeyAsync(step.Namespace, step.SecretName, key, Materialise(step, key, step.Type, run), cancellationToken);
                return;

            default:
                // Reachable only if the planner learns a step kind this executor does not. They
                // are edited in different repositories, so this must stop the run rather than
                // fall through it as a silent success.
                throw new ExecutionRefusedException($"no executor branch for step kind {JsonSerializer.Serialize(step.Kind)} — the planner emits a step this executor does not know");
        }
    }

    private static string Materialise(IPlanStep step, string key, string? type, Run run)
    {
        var entry = EntryFor(run, step, key, type);

        if (type == "random")
        {
            var value = GenerateHex(entry.Bytes ?? DefaultRandomBytes);
            run.Generated.Add(value);
            return value;
        }

        if (type == "postgres-db")
        {
            if (entry.Role is null || !run.Passwords.TryGetValue(entry.Role, out var password))
            {
                // The planner only writes a postgres-db key in a run that also creates its role;
                // anything else it reports as blocked or drift. So this is a plan that contradicts
                // itself, and writing a connection string with a password we invented here would
                // authenticate against nothing.
                throw new ExecutionRefusedException($"refusing to execute: the plan writes {step.Namespace}/{step.SecretName}#{key} but does not create role {entry.Role} in the same run, so there is no password to put in it");
            }

            var value = NpgsqlConnectionString(run.Host, run.Port, entry.Database, entry.Role, password);
            run.Generated.Add(value);
            return value;
        }

        // `external` reaches here only if the planner emitted a step for a value nothing can
        // generate — which it never does; it blocks them. Generating something would put a value
        // into a Secret that a human is supposed to supply, and create-only means nobody could
        // replace it.
        throw new ExecutionRefusedException($"refusing to execute: {step.Namespace}/{step.SecretName}#{key} is type {JsonSerializer.Serialize(type)}, which nothing here can generate");
    }

    private static string Required(string? value, IPlanStep step, string field) =>
        string.IsNullOrEmpty(value)
            ? throw new ExecutionRefusedException($"refusing to execute: a {step.Kind} step has no {field}")
            : value;

    /// <summary>
    /// What a step becomes in the report: its identity and its kind, and no value. A step is never
    /// copied into the report wholesale. `create-secret` carries a `Keys` list, and a future field
    /// on it would be copied out by a spread without anyone deciding to — this lists what goes in.
    /// </summary>
    private static ExecutedStep Describe(IPlanStep step) => step.Kind switch
    {
        "create-role" => new ExecutedStep { Kind = step.Kind, Detail = step.Detail, Role = step.Role },
        "create-database" => new ExecutedStep { Kind = step.Kind, Detail = step.Detail, Database = step.Database, Owner = step.Role },
        "create-secret" => new ExecutedStep { Kind = step.Kind, Detail = step.Detail, Namespace = step.Namespace, SecretName = step.SecretName, Keys = (step.Keys ?? []).Select(k => k.Key).ToList() },
        "patch-secret-key" => new ExecutedStep { Kind = step.Kind, Detail = step.Detail, Namespace = step.Namespace, SecretName = step.SecretName, Key = step.Key },
        _ => new ExecutedStep { Kind = step.Kind, Detail = step.Detail },
    };

    private sealed class Run(IReadOnlyDictionary<string, IKeyDeclaration> index, string host, int port)
    {
        public IReadOnlyDictionary<string, IKeyDeclaration> Index { get; } = index;

        public string Host { get; } = host;

        public int Port { get; } = port;

        /// <summary>role -> the password it was created with.</summary>
        public Dictionary<string, string> Passwords { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Generated { get; } = new(StringComparer.Ordinal);
    }
}
