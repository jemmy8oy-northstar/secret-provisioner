// Run with: npm test  (i.e. bare `node --test`, which auto-discovers)
//
// ⚠️ NOT `node --test src/`. Naming a directory makes Node resolve it as a
// module to execute and fail with MODULE_NOT_FOUND, which looks exactly like a
// broken test suite. Checked, not assumed — that is how this comment got its
// second line.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { keysOf, indexSecrets, indexPostgres, observation } from './observe.js';

// A Secret shaped the way the Kubernetes API actually returns one — values
// present, base64-encoded, sitting immediately beside the names we want.
const liveSecret = () => ({
  metadata: { name: 'around-the-world-secrets', namespace: 'balenthiran' },
  type: 'Opaque',
  data: {
    ConnectionStrings__DefaultConnection: 'SG9zdD1wZztQYXNzd29yZD1odW50ZXIy',
    Jwt__Secret: 'ZGVhZGJlZWZkZWFkYmVlZg==',
    Admin__Key: 'YWRtaW4ta2V5',
  },
});

test('reads key names and nothing else', () => {
  assert.deepEqual(keysOf(liveSecret()), ['Admin__Key', 'ConnectionStrings__DefaultConnection', 'Jwt__Secret']);
});

test('no value survives the observer, in any shape', () => {
  // The property worth asserting rather than intending: the values above are
  // one character away from being returned (`Object.keys(data)` vs `data`), and
  // a value that reaches the observation can reach a log line and then a public
  // repo. Serialise the whole thing and look for them.
  const secret = liveSecret();
  const obs = observation([{ namespace: 'balenthiran', listing: { items: [secret] } }], { roles: [], databases: [] });
  const serialised = JSON.stringify(obs);

  for (const value of Object.values(secret.data)) {
    assert.ok(!serialised.includes(value), `the observation leaked ${value}`);
  }
  // …and it did carry the names, so the test above is not passing by emptiness.
  assert.ok(serialised.includes('Jwt__Secret'));
});

test('stringData counts as present, not missing', () => {
  // A Secret written from a manifest can carry stringData. Reading only `data`
  // would report the key as absent and the planner would plan to patch a key
  // that is already there — writing over a value, which is the one thing
  // create-only forbids.
  const secret = { metadata: { name: 's' }, data: { A: 'eA==' }, stringData: { B: 'plain' } };
  assert.deepEqual(keysOf(secret), ['A', 'B']);
});

test('a Secret with no name is skipped; the namespace still counts as read', () => {
  const obs = observation(
    [{ namespace: 'balenthiran', listing: { items: [{ metadata: {} }, liveSecret()] } }],
    { roles: [], databases: [] },
  );
  assert.deepEqual(Object.keys(obs.secrets), ['balenthiran/around-the-world-secrets']);
  assert.deepEqual(obs.namespaces, ['balenthiran']);
});

test('an empty namespace is enumerated with no secrets — that is the whole point', () => {
  const obs = observation([{ namespace: 'balenthiran', listing: { items: [] } }], { roles: [], databases: [] });
  assert.deepEqual(obs.namespaces, ['balenthiran']);
  assert.deepEqual(obs.secrets, {});
});

// ---------------------------------------------------------------------------
// The seam: a failed listing must not look like an empty namespace
// ---------------------------------------------------------------------------

test('a namespace whose listing FAILED is not claimed as enumerated', () => {
  const obs = observation(
    [
      { namespace: 'balenthiran', error: new Error('connect ETIMEDOUT') },
      { namespace: 'other', listing: { items: [] } },
    ],
    { roles: [], databases: [] },
  );

  // If `balenthiran` appeared here, the planner would read "no Secrets in
  // balenthiran" and plan to CREATE around-the-world-secrets over the live one,
  // deleting the four keys it is not writing.
  assert.deepEqual(obs.namespaces, ['other']);
  assert.deepEqual(obs.secrets, {});
  assert.deepEqual(obs.failures, [{ namespace: 'balenthiran', error: 'connect ETIMEDOUT' }]);
});

test('every namespace failing yields an observation that is honest, not empty-looking', () => {
  const obs = observation([{ namespace: 'balenthiran', error: new Error('403') }], null);
  assert.deepEqual(obs.namespaces, []);
  assert.equal(obs.postgres, null);
  assert.equal(obs.failures.length, 1);
});

test('a falsy-but-real error still counts as a failure', () => {
  // `if (result.error)` would let an error of 0 or '' through as a success.
  const obs = observation([{ namespace: 'balenthiran', error: '' }], null);
  assert.deepEqual(obs.namespaces, []);
  assert.equal(obs.failures.length, 1);
});

// ---------------------------------------------------------------------------
// Postgres catalogues
// ---------------------------------------------------------------------------

test('system roles and databases are not mistaken for ours', () => {
  const roles = { rows: [{ rolname: 'postgres' }, { rolname: 'pg_read_all_data' }, { rolname: 'aroundtheworld' }] };
  const dbs = { rows: [{ datname: 'template0' }, { datname: 'template1' }, { datname: 'postgres' }, { datname: 'aroundtheworld' }] };

  // Without this, `template1` reads as a database name already taken, and the
  // planner blocks an app that could have been provisioned.
  assert.deepEqual(indexPostgres(roles, dbs), { roles: ['aroundtheworld'], databases: ['aroundtheworld'] });
});

test('an empty catalogue is empty, not an error', () => {
  assert.deepEqual(indexPostgres({ rows: [] }, { rows: [] }), { roles: [], databases: [] });
});

test('postgres: null travels through untouched — not inspected is not empty', () => {
  const obs = observation([{ namespace: 'balenthiran', listing: { items: [] } }], null);
  assert.equal(obs.postgres, null);
});

// ---------------------------------------------------------------------------
// Shape
// ---------------------------------------------------------------------------

test('the observation is exactly what the planner demands', () => {
  const obs = observation([{ namespace: 'balenthiran', listing: { items: [liveSecret()] } }], { roles: ['r'], databases: ['d'] });

  assert.deepEqual(Object.keys(obs).sort(), ['failures', 'namespaces', 'postgres', 'secrets']);
  assert.ok(Array.isArray(obs.namespaces));
  for (const keys of Object.values(obs.secrets)) assert.ok(Array.isArray(keys));
});

test('indexing is deterministic and duplicate namespaces collapse', () => {
  const obs = observation(
    [
      { namespace: 'balenthiran', listing: { items: [liveSecret()] } },
      { namespace: 'balenthiran', listing: { items: [] } },
    ],
    null,
  );
  assert.deepEqual(obs.namespaces, ['balenthiran']);
});
