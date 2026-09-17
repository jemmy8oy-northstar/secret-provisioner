// Run with: npm test  (i.e. bare `node --test`, which auto-discovers)
//
// ⚠️ NOT `node --test src/` — see the note at the top of observe.test.js.
//
// THE PLAN FIXTURES ARE THE REAL PLANNER'S OUTPUT, NOT A DRAWING OF IT.
// `fixtures/*.plan.json` were produced by running
// oke-fleet:scripts/plan-secrets.mjs (commit 34c7d8c on `feat/secret-plan`)
// over oke-fleet's actual `secrets/*.json`, and `fixtures/declarations.json` is
// what its validator returned in the same run. That matters because this whole
// module is one side of a seam that spans two repositories: a hand-written
// fixture tests my memory of the plan shape, which is exactly the thing most
// likely to be wrong. Regenerate them with:
//
//   node -e "..." # see src/fixtures/README.md
//
// and if a regenerated fixture changes, that IS the news.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  execute,
  generateHex,
  npgsqlConnectionString,
  indexDeclarations,
  redactValues,
  assertNoGeneratedValues,
  DEFAULT_POSTGRES_PORT,
} from './execute.js';

const fixture = (name) => JSON.parse(readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8'));
const emptyEstate = () => fixture('empty-estate.plan.json');
const partialEstate = () => fixture('partial-estate.plan.json');
const declarations = () => fixture('declarations.json');

const config = { postgres: { host: 'pg-postgresql.data.svc.cluster.local' } };

// Records everything it was asked to do, including the values — which is the
// point: the test can then assert the values went to the cluster and NOT to the
// report. A fake that discarded them could not tell those two apart.
function recorder(overrides = {}) {
  const calls = { createRole: [], createDatabase: [], createSecret: [], patchSecretKey: [] };
  const effects = {};
  for (const name of Object.keys(calls)) {
    effects[name] = async (args) => {
      calls[name].push(args);
      if (overrides[name]) return overrides[name](args);
    };
  }
  return { effects, calls };
}

test('generateHex produces only hex, of the length asked for', () => {
  const value = generateHex(32);
  assert.equal(value.length, 64);
  assert.match(value, /^[0-9a-f]+$/);
  assert.notEqual(generateHex(32), generateHex(32));
});

test('generateHex refuses a byte count that is not a positive integer', () => {
  for (const bad of [0, -1, 1.5, '32', null, undefined, NaN]) {
    assert.throws(() => generateHex(bad), /positive integer/);
  }
});

test('the connection string is Npgsql keyword syntax, which is what the apps consume', () => {
  // Not a postgres:// URL. `DATABASE_URL` is a key NAME — balenthiran.co.uk
  // binds that key to the env var ConnectionStrings__DefaultConnection, and
  // both apps use Npgsql.EntityFrameworkCore.PostgreSQL.
  assert.equal(
    npgsqlConnectionString({ host: 'pg', port: 5432, database: 'app', username: 'app', password: 'abc123' }),
    'Host=pg;Port=5432;Database=app;Username=app;Password=abc123',
  );
});

test('the connection string refuses a component it would have to quote', () => {
  const base = { host: 'pg', port: 5432, database: 'app', username: 'app', password: 'abc123' };
  for (const field of ['host', 'database', 'username', 'password']) {
    for (const bad of ['a;b', "a'b", 'a"b', 'a=b']) {
      assert.throws(
        () => npgsqlConnectionString({ ...base, [field]: bad }),
        /does not quote/,
        `${field} = ${bad} should have been refused`,
      );
    }
  }
});

test('the connection string refuses a missing component rather than writing "undefined"', () => {
  assert.throws(() => npgsqlConnectionString({ port: 5432, database: 'a', username: 'b', password: 'c' }), /Host is required/);
  assert.throws(() => npgsqlConnectionString({ host: 'p', port: 5432, database: 'a', username: 'b' }), /Password is required/);
});

test('the port is validated before it is coerced, not after', () => {
  // The check `typeof value !== 'string'` runs over a parts object in which
  // Port has ALREADY been through String(). Put the coercion first and that one
  // field's validation is dead: Port=NaN and Port=[object Object] both compose
  // cleanly. NaN is the realistic one — Number(process.env.PG_PORT) on an unset
  // variable is NaN, not undefined, so a `?? DEFAULT` upstream does not catch
  // it, and create-only means the resulting Secret can never be rewritten.
  const base = { host: 'pg', database: 'app', username: 'app', password: 'abc123' };
  for (const bad of [NaN, undefined, null, '5432', 5432.5, 0, -1, 65536, {}, []]) {
    assert.throws(() => npgsqlConnectionString({ ...base, port: bad }), /Port must be an integer/, `port ${String(bad)} should have been refused`);
  }
  assert.match(npgsqlConnectionString({ ...base, port: 5432 }), /;Port=5432;/);
});

test('execute refuses a NaN port rather than creating roles and then writing Port=NaN', async () => {
  const { effects, calls } = recorder();
  await assert.rejects(
    () => execute(emptyEstate(), declarations(), effects, { postgres: { host: 'pg', port: Number('') } }),
    /config\.postgres\.port must be an integer/,
  );
  // Before anything was created, not after.
  assert.equal(calls.createRole.length, 0);
});

test('a hex password never needs quoting — which is why values are hex and not base64', () => {
  // The encoding is a correctness decision: base64 emits `+`, `/` and `=`, and
  // `=` inside a keyword-value string is where the string stops meaning what it
  // looks like it means. 200 samples is enough to catch an encoding change.
  for (let i = 0; i < 200; i += 1) {
    assert.doesNotThrow(() =>
      npgsqlConnectionString({ host: 'pg', port: 5432, database: 'd', username: 'u', password: generateHex(16) }));
  }
});

test('an empty estate: every step runs, in the planner\'s order', async () => {
  const { effects, calls } = recorder();
  const report = await execute(emptyEstate(), declarations(), effects, config);

  assert.equal(report.failed, null);
  assert.equal(report.summary.done, 6);
  assert.equal(report.summary.skipped, 0);
  assert.deepEqual(report.done.map((d) => d.kind), [
    'create-role', 'create-database', 'create-role', 'create-database', 'create-secret', 'create-secret',
  ]);
  // The DDL ran before the writes. A connection string cannot be composed
  // before the role it names exists, and that ordering is the planner's.
  assert.deepEqual(calls.createRole.map((c) => c.role), ['aroundtheworld', 'balenthiran']);
  assert.deepEqual(calls.createDatabase, [
    { database: 'aroundtheworld', owner: 'aroundtheworld' },
    { database: 'balenthiran', owner: 'balenthiran' },
  ]);
});

test('the password put in the connection string is the one the role was created with', async () => {
  // The join this module exists to make. The plan step that writes
  // `#DATABASE_URL` carries no role, no database and no password; get the join
  // wrong and the Secret holds a credential that authenticates against nothing,
  // and create-only means it can never be rewritten.
  const { effects, calls } = recorder();
  await execute(emptyEstate(), declarations(), effects, config);

  const passwords = Object.fromEntries(calls.createRole.map((c) => [c.role, c.password]));
  const atw = calls.createSecret.find((c) => c.name === 'around-the-world-secrets');
  const bal = calls.createSecret.find((c) => c.name === 'balenthiran-secrets');

  assert.equal(
    atw.data.ConnectionStrings__DefaultConnection,
    `Host=${config.postgres.host};Port=${DEFAULT_POSTGRES_PORT};Database=aroundtheworld;Username=aroundtheworld;Password=${passwords.aroundtheworld}`,
  );
  assert.equal(
    bal.data.DATABASE_URL,
    `Host=${config.postgres.host};Port=${DEFAULT_POSTGRES_PORT};Database=balenthiran;Username=balenthiran;Password=${passwords.balenthiran}`,
  );
  // …and not each other's.
  assert.notEqual(passwords.aroundtheworld, passwords.balenthiran);
});

test('the role goes in Username and the database in Database — they are not the same field', async () => {
  // Both live declarations name role === database ("aroundtheworld",
  // "balenthiran"), so NO fixture drawn from the real estate can tell the two
  // apart: swap them and every assertion above still passes. This one is
  // synthetic for exactly that reason. Getting it backwards writes a credential
  // that cannot authenticate, into a Secret create-only can never rewrite.
  const { declarations: decls, plan } = fixture('distinct-role-and-database.json');
  const { effects, calls } = recorder();
  const report = await execute(plan, decls, effects, config);

  assert.equal(report.failed, null);
  assert.equal(calls.createRole[0].role, 'distinct_role');
  assert.deepEqual(calls.createDatabase[0], { database: 'distinct_db', owner: 'distinct_role' });
  assert.match(
    calls.createSecret[0].data.ConnectionStrings__DefaultConnection,
    /;Database=distinct_db;Username=distinct_role;Password=[0-9a-f]{64}$/,
  );
});

test('the byte count comes from the declaration, not from the step\'s prose', async () => {
  // The step says "generate 16 random bytes, hex-encoded (openssl rand -hex
  // 16)" in an English sentence. Admin__Key is declared as 16 bytes and
  // Jwt__Secret as 32; if this module were defaulting both, they would be equal
  // length and the difference here is the only thing that would notice.
  const { effects, calls } = recorder();
  await execute(emptyEstate(), declarations(), effects, config);

  const atw = calls.createSecret.find((c) => c.name === 'around-the-world-secrets');
  assert.equal(atw.data.Jwt__Secret.length, 64, 'Jwt__Secret is declared as 32 bytes');
  assert.equal(atw.data.Admin__Key.length, 32, 'Admin__Key is declared as 16 bytes');
});

test('no generated value reaches the report, in any shape', async () => {
  // The property. The report is what POST /reconcile serialises to an HTTP
  // response, and the difference between reporting a key name and reporting its
  // value is one property access.
  const { effects, calls } = recorder();
  const report = await execute(emptyEstate(), declarations(), effects, config);
  const serialised = JSON.stringify(report);

  const values = [
    ...calls.createRole.map((c) => c.password),
    ...calls.createSecret.flatMap((c) => Object.values(c.data)),
  ];
  assert.equal(values.length, 6, 'the run must actually have generated something');
  for (const value of values) assert.ok(!serialised.includes(value), 'a generated value is in the report');

  // Every one of those six is REGISTERED, not merely absent. The distinction
  // matters because `redactValues` has a shape pass that would scrub a hex
  // password out of an error message whether or not we tracked it — so absence
  // from the report is no longer evidence that `assertNoGeneratedValues` is
  // actually covering it. This count is what makes the registration observable;
  // without it, dropping a `generated.add` is invisible.
  assert.equal(report.summary.generated, 6, 'a generated value was not registered, so the final assertion does not cover it');

  // …and the report did carry the identities, so the assertion above is not
  // passing by emptiness.
  assert.match(serialised, /around-the-world-secrets/);
  assert.match(serialised, /Jwt__Secret/);
  assert.match(serialised, /aroundtheworld/);
});

test('a create-secret step reports key NAMES, and the report has no data field at all', async () => {
  const { effects } = recorder();
  const report = await execute(emptyEstate(), declarations(), effects, config);
  const created = report.done.find((d) => d.kind === 'create-secret' && d.secretName === 'around-the-world-secrets');

  assert.deepEqual(created.keys, ['ConnectionStrings__DefaultConnection', 'Jwt__Secret', 'Admin__Key']);
  assert.equal(created.data, undefined);
});

test('a partial estate patches one key and never rewrites the Secret', async () => {
  // around-the-world-secrets already holds five keys. Writing it whole to add
  // Jwt__Secret deletes the other four — two of which are Admin__Key and the
  // live connection string — and the visible symptom is every guest signed out
  // mid-party, not an error.
  const { effects, calls } = recorder();
  const report = await execute(partialEstate(), declarations(), effects, config);

  assert.equal(report.failed, null);
  assert.equal(calls.patchSecretKey.length, 1);
  assert.equal(calls.patchSecretKey[0].namespace, 'balenthiran');
  assert.equal(calls.patchSecretKey[0].name, 'around-the-world-secrets');
  assert.equal(calls.patchSecretKey[0].key, 'Jwt__Secret');
  assert.match(calls.patchSecretKey[0].value, /^[0-9a-f]{64}$/);
  // Nothing was created in that namespace's existing Secret.
  assert.deepEqual(calls.createSecret.map((c) => c.name), ['balenthiran-secrets']);
});

test('the first failure stops the run, and the rest is reported as not attempted', async () => {
  const { effects, calls } = recorder({
    createDatabase: () => { throw new Error('pq: permission denied to create database'); },
  });
  const report = await execute(emptyEstate(), declarations(), effects, config);

  assert.equal(report.summary.done, 1);
  assert.equal(report.failed.step.kind, 'create-database');
  assert.match(report.failed.error, /permission denied/);
  assert.equal(report.summary.skipped, 4);
  assert.deepEqual(report.skipped.map((s) => s.kind), ['create-role', 'create-database', 'create-secret', 'create-secret']);
  // Not attempted means NOT ATTEMPTED — no Secret was written after the failure.
  assert.equal(calls.createSecret.length, 0);
});

test('a password in an error message is redacted before it is recorded', async () => {
  // Postgres quotes the failing statement back at you, and for CREATE ROLE the
  // statement contains the password. Without this the report — a public HTTP
  // response — carries the credential that was just created.
  let leaked;
  const { effects } = recorder({
    createRole: ({ password }) => {
      leaked = password;
      throw new Error(`pq: syntax error at or near "CREATE ROLE aroundtheworld LOGIN PASSWORD '${password}'"`);
    },
  });
  const report = await execute(emptyEstate(), declarations(), effects, config);

  assert.ok(leaked, 'the effect must have seen a password for this test to mean anything');
  assert.ok(!JSON.stringify(report).includes(leaked));
  assert.match(report.failed.error, /<redacted>/);
  assert.match(report.failed.error, /syntax error/, 'the diagnostic itself survives redaction');
});

test('redactValues replaces the longest value first', () => {
  // A value that contains another must not be half-replaced into something that
  // no longer matches and then survives.
  const values = new Set(['abcd', 'abcdef']);
  assert.equal(redactValues('x abcdef y abcd z', values), 'x <redacted> y <redacted> z');
});

test('redactValues also catches a value the driver truncated', () => {
  // Exact matching has one blind spot and it is the realistic one: a message
  // that quotes only part of a value no longer equals anything we hold, so it
  // sails through pass 1 while still carrying most of the entropy. The shape
  // pass exists for that. The schema's floor is 16 bytes (oke-fleet's
  // MIN_RANDOM_BYTES), so 32 hex characters is the shortest thing we can make.
  const full = generateHex(32);
  const truncated = full.slice(0, 48);
  const out = redactValues(`pq: error near "${truncated}…"`, new Set([full]));

  assert.ok(!out.includes(truncated), 'a truncated value survived redaction');
  assert.match(out, /<redacted>/);
  assert.match(out, /pq: error near/, 'the diagnostic survives');
});

test('redactValues leaves ordinary prose alone', () => {
  // The shape pass must not eat the diagnostic it is protecting. Namespaces,
  // key names and Postgres identifiers are not 32-character hex runs.
  const text = 'pq: role "aroundtheworld" already exists — balenthiran/around-the-world-secrets#Jwt__Secret deadbeef';
  assert.equal(redactValues(text, new Set()), text);
});

test('assertNoGeneratedValues throws on a report that leaks — called directly', () => {
  // Unreachable through execute() by construction, and a guard no test can
  // execute is indistinguishable from one that does not work.
  const values = new Set(['deadbeef']);
  assert.doesNotThrow(() => assertNoGeneratedValues({ done: [{ key: 'Jwt__Secret' }] }, values));
  assert.throws(() => assertNoGeneratedValues({ done: [{ note: 'wrote deadbeef' }] }, values), /reached the report/);
  // Nested anywhere, not just in the fields anyone thought of.
  assert.throws(() => assertNoGeneratedValues({ a: { b: [{ c: 'xxdeadbeefxx' }] } }, values), /reached the report/);
});

test('refuses to run without a Postgres host rather than guessing one', async () => {
  const { effects, calls } = recorder();
  await assert.rejects(() => execute(emptyEstate(), declarations(), effects, {}), /postgres\.host is required/);
  await assert.rejects(() => execute(emptyEstate(), declarations(), effects, { postgres: { host: '' } }), /postgres\.host is required/);
  assert.equal(calls.createRole.length, 0);
});

test('refuses a plan that writes a key no declaration claims', async () => {
  const plan = emptyEstate();
  plan.steps.at(-1).keys[0].key = 'DATABASE_URL_TYPO';
  const { effects } = recorder();
  const report = await execute(plan, declarations(), effects, config);

  assert.match(report.failed.error, /which no declaration claims/);
});

test('refuses a plan whose type disagrees with the declaration', async () => {
  // The two are joined on identity alone, so the type is the only thing that
  // can say "these came from different reads".
  const decls = declarations();
  decls.find((d) => d.app === 'around-the-world').keys.find((k) => k.key === 'Jwt__Secret').type = 'external';
  const { effects } = recorder();
  const report = await execute(emptyEstate(), decls, effects, config);

  assert.match(report.failed.error, /the plan writes .*Jwt__Secret as random, the declaration says external/);
});

test('refuses to write a connection string for a role this run did not create', async () => {
  // The planner never emits this — a postgres-db key whose role already exists
  // is reported as blocked. So it is a plan contradicting itself, and inventing
  // a password here would produce a credential that authenticates against
  // nothing, in a Secret create-only can never rewrite.
  const plan = emptyEstate();
  plan.steps = plan.steps.filter((s) => s.kind !== 'create-role');
  const { effects } = recorder();
  const report = await execute(plan, declarations(), effects, config);

  assert.match(report.failed.error, /does not create role aroundtheworld in the same run/);
});

test('refuses a plan that creates the same role twice', async () => {
  // Left alone, the second password overwrites the first in the map while the
  // FIRST is the one the server was given — so the connection string would be
  // composed from a password that was never set, in a Secret create-only can
  // never rewrite.
  const plan = emptyEstate();
  plan.steps = [plan.steps[0], { ...plan.steps[0] }, ...plan.steps.slice(1)];
  const { effects } = recorder();
  const report = await execute(plan, declarations(), effects, config);

  assert.match(report.failed.error, /creates role aroundtheworld twice/);
});

test('refuses a step kind it does not know', async () => {
  // The planner and this executor live in different repositories. A fifth kind
  // added there must stop the run, not fall through it as a silent success.
  const plan = emptyEstate();
  plan.steps = [{ kind: 'delete-secret', namespace: 'balenthiran', secretName: 'around-the-world-secrets', detail: 'nope' }];
  const { effects, calls } = recorder();
  const report = await execute(plan, declarations(), effects, config);

  assert.match(report.failed.error, /no executor branch for step kind "delete-secret"/);
  assert.equal(calls.createSecret.length, 0);
});

test('refuses to generate an external value even if a plan asks for one', async () => {
  const plan = emptyEstate();
  plan.steps.at(-1).keys = [{ key: 'DATABASE_URL', type: 'external', source: 'nothing can make this' }];
  const decls = declarations();
  decls.find((d) => d.app === 'balenthiran').keys[0] = { key: 'DATABASE_URL', type: 'external' };
  const { effects } = recorder();
  const report = await execute(plan, decls, effects, config);

  assert.match(report.failed.error, /type "external", which nothing here can generate/);
});

test('the planner\'s refusals are carried through verbatim, and do not stop anything', async () => {
  // blocked/unknown/drift/unmanaged are decisions the planner already made.
  // Restating them in the executor's words would be a second chance to get them
  // wrong; treating them as failures would mean one un-generatable API key
  // blocks every other app's provisioning.
  const plan = emptyEstate();
  const { effects } = recorder();
  const report = await execute(plan, declarations(), effects, config);

  assert.deepEqual(report.blocked, plan.blocked);
  assert.equal(report.summary.blocked, 3);
  assert.equal(report.failed, null);
  assert.equal(report.summary.done, 6);
});

test('noop is carried through too — a settled estate is mostly noop', async () => {
  // `noop` and `unmanaged` are the same kind of thing: an existing key nothing
  // touched. Carrying one and dropping the other makes a run that correctly did
  // nothing look like a run that saw nothing — and on a provisioned estate that
  // is nearly every run.
  const plan = partialEstate();
  assert.equal(plan.noop.length, 5, 'the fixture must carry noop entries for this to mean anything');

  const { effects } = recorder();
  const report = await execute(plan, declarations(), effects, config);

  assert.deepEqual(report.noop, plan.noop);
  assert.equal(report.summary.noop, 5);
  assert.equal(report.summary.unmanaged, 1);
});

test('indexDeclarations keys on exactly the identity a plan step carries', () => {
  const index = indexDeclarations(declarations());
  assert.equal(index.get('balenthiran/around-the-world-secrets#Admin__Key').bytes, 16);
  assert.equal(index.get('balenthiran/balenthiran-secrets#DATABASE_URL').role, 'balenthiran');
  assert.equal(index.get('balenthiran/nope#nope'), undefined);
  assert.deepEqual(indexDeclarations(undefined), new Map());
});

test('an empty plan is a clean run, not an error', async () => {
  const { effects } = recorder();
  const report = await execute({ steps: [] }, declarations(), effects, config);
  assert.deepEqual(report.summary, { done: 0, failed: 0, skipped: 0, generated: 0, blocked: 0, unknown: 0, drift: 0, unmanaged: 0, noop: 0 });
});
