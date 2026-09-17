// The executor: takes the plan oke-fleet's planner produced and actually does
// it. Slice 3 of claude-code-bot#97.
//
// The effects live behind an injected `effects` object, so everything here —
// the ordering, the create-only guarantees, the value generation, the
// composition of a connection string, and above all what does and does not
// reach the report — is decided by a test rather than by a live cluster. The
// Kubernetes and Postgres clients that implement `effects` for real arrive in
// the next slice, with their dependencies.
//
// THE PLAN SAYS WHAT. THE DECLARATIONS SAY WITH WHAT.
// A step carries the decision and its identity — "patch balenthiran/
// balenthiran-secrets#DATABASE_URL" — but not the parameters, because the
// planner is a decision record and `source` is prose written for a human
// ("generate 32 random bytes, hex-encoded"). Deriving a cryptographic
// parameter by parsing an English sentence is not a thing to do, so `execute`
// takes the declarations as well and resolves `bytes`, `role` and `database`
// from the entry the step names. Anything the two disagree about — a step with
// no declaration, or a declaration of a different type — stops the run rather
// than being reconciled, because that disagreement means the plan and the
// declarations came from different reads and neither is now trustworthy.
//
// NO VALUE IS EVER REPORTED, AND THAT IS CHECKED RATHER THAN INTENDED
// Everything this module generates is a secret. The report it returns is the
// only thing that leaves, it is what `GET /status` and `POST /reconcile` will
// serialise to an HTTP response, and the difference between reporting a key
// name and reporting a key's value is one property access. So:
//   - every error message is passed through `redactValues` before it is
//     recorded, because a Postgres error can quote the statement that failed
//     and that statement contains a password;
//   - `assertNoGeneratedValues` then walks the finished report and throws if
//     any generated value survived anywhere in it.
// The first is the mechanism; the second is the proof, and it covers reports
// produced by code that is not written yet.
//
// CREATE-ONLY IS ENFORCED BY THE SERVER, NOT BY A CHECK HERE
// The plan was made against an observation taken some moments earlier. Between
// the two, a Secret can appear. So `effects.createSecret` must be a CREATE
// (409 Conflict if it exists), never an apply or a replace, and
// `effects.createRole` must be a plain `CREATE ROLE` (42710 if it exists). A
// check-then-act here would be a race; letting the server refuse is not.
// A refusal is a failure, and a failure stops the run — see `execute`.
import { randomBytes } from 'node:crypto';

export const DEFAULT_RANDOM_BYTES = 32;
export const DEFAULT_POSTGRES_PORT = 5432;

// Npgsql's connection string is keyword=value pairs separated by `;`, so a
// value containing `;`, `'`, `"` or `=` has to be quoted and escaped. We never
// do that. Instead every component is required to be free of those characters,
// and the run stops if one is not.
//
// This is why values are generated as HEX and not base64: hex is [0-9a-f], so a
// generated password can never need quoting. base64 would put `+`, `/` and `=`
// into passwords, and `=` inside a keyword-value string is where a connection
// string stops meaning what it looks like it means. The encoding is a
// correctness decision, not a cosmetic one.
const UNSAFE_IN_CONNECTION_STRING = /[;'"=]/;

/**
 * A secret value: `bytes` random bytes, hex-encoded.
 *
 * `crypto.randomBytes` is a CSPRNG, unseeded, and that is the whole
 * implementation. This repo is public (James, claude-code-bot#97), so the
 * generator has to be safe to read — a generator that is weak when published
 * was weak when it was private.
 *
 * @param {number} bytes
 * @returns {string} 2 * bytes hex characters
 */
export function generateHex(bytes) {
  if (!Number.isInteger(bytes) || bytes < 1) {
    throw new TypeError(`refusing to generate a value of ${JSON.stringify(bytes)} bytes — a declaration's bytes must be a positive integer`);
  }
  return randomBytes(bytes).toString('hex');
}

/**
 * The Npgsql keyword-value connection string the apps in this estate consume.
 *
 * Measured, not assumed: `around-the-world-secrets#ConnectionStrings__Default-
 * Connection` and `balenthiran-secrets#DATABASE_URL` are both bound to the .NET
 * env var `ConnectionStrings__DefaultConnection` (balenthiran.co.uk:helm/
 * values.yaml maps the key to that name), and both apps use
 * Npgsql.EntityFrameworkCore.PostgreSQL. `DATABASE_URL` is a key NAME, not a
 * format: writing a `postgres://` URL into it because of what it is called
 * produces a Secret that looks entirely reasonable and an app that will not
 * start, with a value nobody is allowed to read in order to find out why.
 */
export function npgsqlConnectionString({ host, port, database, username, password }) {
  const parts = { Host: host, Port: String(port), Database: database, Username: username, Password: password };
  for (const [keyword, value] of Object.entries(parts)) {
    if (typeof value !== 'string' || value === '') {
      throw new TypeError(`connection string: ${keyword} is required`);
    }
    if (UNSAFE_IN_CONNECTION_STRING.test(value)) {
      // Deliberately does not name the offending value — this throws on the
      // Password branch too.
      throw new Error(`connection string: ${keyword} contains one of ; ' " = and this composer does not quote. Refusing rather than emitting a string that parses as something else`);
    }
  }
  return Object.entries(parts).map(([k, v]) => `${k}=${v}`).join(';');
}

/**
 * Index the declarations by the identity a plan step carries.
 *
 * @returns {Map<string, object>} `<namespace>/<secretName>#<key>` -> the key entry
 */
export function indexDeclarations(declarations) {
  const index = new Map();
  for (const decl of declarations ?? []) {
    for (const entry of decl.keys ?? []) {
      index.set(`${decl.namespace}/${decl.secretName}#${entry.key}`, entry);
    }
  }
  return index;
}

/**
 * The declaration entry a write step names — or a refusal.
 *
 * The type is re-checked because the two artefacts are joined on identity
 * alone: a plan that says `random` where the declaration now says `external`
 * means the declarations changed between planning and executing, and the safe
 * response to that is to stop, not to pick one.
 */
function entryFor(index, step, key, type) {
  const at = `${step.namespace}/${step.secretName}#${key}`;
  const entry = index.get(at);
  if (entry === undefined) {
    throw new Error(`refusing to execute: the plan writes ${at}, which no declaration claims — the plan and the declarations came from different reads`);
  }
  if (entry.type !== type) {
    throw new Error(`refusing to execute: the plan writes ${at} as ${type}, the declaration says ${entry.type} — the declarations changed after the plan was made`);
  }
  return entry;
}

/**
 * Replace every generated value in a string with a marker.
 *
 * Applied to error messages, because a Postgres driver error can quote the
 * failing statement and that statement contains a password. Longest first, so a
 * value that contains another is not partially replaced into something that no
 * longer matches.
 */
export function redactValues(text, values) {
  let out = String(text);
  for (const value of [...values].sort((a, b) => b.length - a.length)) {
    if (value.length === 0) continue;
    out = out.split(value).join('<redacted>');
  }
  return out;
}

/**
 * The property this module exists to hold: nothing it generated is in what it
 * returns.
 *
 * Exported and called directly by its tests, because through `execute` it is
 * unreachable by construction — and a guard no test can execute is
 * indistinguishable from one that does not work. It walks the serialised report
 * rather than known fields, so a field added later by someone who has not read
 * this comment is covered too.
 */
export function assertNoGeneratedValues(report, values) {
  const serialised = JSON.stringify(report);
  for (const value of values) {
    if (value.length > 0 && serialised.includes(value)) {
      throw new Error(`provisioner bug: a generated secret value reached the report. ${values.size ?? values.length} value(s) generated this run; at least one is in the ${serialised.length}-character report that POST /reconcile would return`);
    }
  }
}

/**
 * Execute a plan.
 *
 * Steps run in the order the planner emitted them — Postgres DDL before the
 * Secret writes, because a connection string cannot be composed before the role
 * it names exists. That ordering is the planner's decision and is not
 * re-derived here.
 *
 * THE FIRST FAILURE STOPS THE RUN. A failed step means the cluster is not what
 * the plan assumed, and every later step was chosen on that assumption; the
 * executor cannot tell which of them depended on it without re-implementing the
 * planner. Stopping costs a re-run, which is free — `/reconcile` is on demand
 * with a human watching (James, claude-code-bot#97) and every step is
 * create-only, so nothing is half-written beyond what the report names.
 *
 * `blocked`, `unknown`, `drift` and `unmanaged` are carried through untouched.
 * They are not failures and they do not stop anything: they are the things the
 * planner decided a provisioner must not do, and the executor's job is to
 * report them, not to reconsider them.
 *
 * @param {object} plan          from oke-fleet:scripts/plan-secrets.mjs
 * @param {object[]} declarations the same declarations the plan was made from
 * @param {object} effects       { createRole, createDatabase, createSecret, patchSecretKey }
 * @param {object} config        { postgres: { host, port? } }
 */
export async function execute(plan, declarations, effects, config) {
  const host = config?.postgres?.host;
  if (typeof host !== 'string' || host === '') {
    // No default. A guessed host composes a connection string that is
    // syntactically perfect and resolves to nothing, and create-only means the
    // wrong value can never be corrected in place.
    throw new TypeError('config.postgres.host is required — refusing to guess the Postgres host into a connection string that can never be rewritten');
  }
  const port = config.postgres.port ?? DEFAULT_POSTGRES_PORT;

  const index = indexDeclarations(declarations);
  // Run-scoped and in memory only. A password is generated when its role is
  // created and read again when its connection string is composed; it is in no
  // other place, and it is unreachable from the report by construction.
  const passwords = new Map();
  const generated = new Set();

  const done = [];
  const skipped = [];
  let failed = null;

  for (const step of plan.steps ?? []) {
    if (failed !== null) {
      skipped.push(describe(step));
      continue;
    }

    try {
      await runStep(step, { index, effects, passwords, generated, host, port });
      done.push(describe(step));
    } catch (error) {
      failed = {
        step: describe(step),
        error: redactValues(error?.message ?? error, generated),
      };
    }
  }

  const report = {
    done,
    failed,
    skipped,
    // Verbatim from the plan. These are the planner's refusals, and restating
    // them in the executor's words would be a second chance to get them wrong.
    blocked: plan.blocked ?? [],
    unknown: plan.unknown ?? [],
    drift: plan.drift ?? [],
    unmanaged: plan.unmanaged ?? [],
    summary: {
      done: done.length,
      failed: failed === null ? 0 : 1,
      skipped: skipped.length,
      generated: generated.size,
      blocked: (plan.blocked ?? []).length,
      unknown: (plan.unknown ?? []).length,
      drift: (plan.drift ?? []).length,
      unmanaged: (plan.unmanaged ?? []).length,
    },
  };

  assertNoGeneratedValues(report, generated);
  return report;
}

async function runStep(step, ctx) {
  switch (step.kind) {
    case 'create-role': {
      const password = generateHex(DEFAULT_RANDOM_BYTES);
      ctx.generated.add(password);
      // Recorded before the call, not after: if `createRole` throws having
      // nonetheless created the role, the password must still be redactable out
      // of the error message.
      ctx.passwords.set(step.role, password);
      await ctx.effects.createRole({ role: step.role, password });
      return;
    }

    case 'create-database':
      await ctx.effects.createDatabase({ database: step.database, owner: step.role });
      return;

    case 'create-secret': {
      const data = {};
      for (const k of step.keys ?? []) data[k.key] = materialise(step, k.key, k.type, ctx);
      await ctx.effects.createSecret({ namespace: step.namespace, name: step.secretName, data });
      return;
    }

    case 'patch-secret-key':
      await ctx.effects.patchSecretKey({
        namespace: step.namespace,
        name: step.secretName,
        key: step.key,
        value: materialise(step, step.key, step.type, ctx),
      });
      return;

    default:
      // Reachable only if the planner learns a step kind this executor does
      // not. They are edited in different repositories, so this must stop the
      // run rather than fall through it as a silent success.
      throw new Error(`no executor branch for step kind ${JSON.stringify(step.kind)} — the planner emits a step this executor does not know`);
  }
}

function materialise(step, key, type, ctx) {
  const entry = entryFor(ctx.index, step, key, type);

  if (type === 'random') {
    const value = generateHex(entry.bytes ?? DEFAULT_RANDOM_BYTES);
    ctx.generated.add(value);
    return value;
  }

  if (type === 'postgres-db') {
    const password = ctx.passwords.get(entry.role);
    if (password === undefined) {
      // The planner only writes a postgres-db key in a run that also creates
      // its role; anything else it reports as blocked or drift. So this is a
      // plan that contradicts itself, and writing a connection string with a
      // password we invented here would authenticate against nothing.
      throw new Error(`refusing to execute: the plan writes ${step.namespace}/${step.secretName}#${key} but does not create role ${entry.role} in the same run, so there is no password to put in it`);
    }
    const value = npgsqlConnectionString({
      host: ctx.host,
      port: ctx.port,
      database: entry.database,
      username: entry.role,
      password,
    });
    ctx.generated.add(value);
    return value;
  }

  // `external` reaches here only if the planner emitted a step for a value
  // nothing can generate — which it never does; it blocks them. Generating
  // something would put a value into a Secret that a human is supposed to
  // supply, and create-only means nobody could replace it.
  throw new Error(`refusing to execute: ${step.namespace}/${step.secretName}#${key} is type ${JSON.stringify(type)}, which nothing here can generate`);
}

/**
 * What a step becomes in the report: its identity and its kind, and no value.
 *
 * A step is never spread into the report wholesale. `create-secret` carries a
 * `keys` array, and a future field on it would be copied out by a spread
 * without anyone deciding to — this lists what goes in instead.
 */
function describe(step) {
  const base = { kind: step.kind, detail: step.detail };
  switch (step.kind) {
    case 'create-role':
      return { ...base, role: step.role };
    case 'create-database':
      return { ...base, database: step.database, owner: step.role };
    case 'create-secret':
      return { ...base, namespace: step.namespace, secretName: step.secretName, keys: (step.keys ?? []).map((k) => k.key) };
    case 'patch-secret-key':
      return { ...base, namespace: step.namespace, secretName: step.secretName, key: step.key };
    default:
      return base;
  }
}
