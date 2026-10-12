#!/usr/bin/env node
// Generates the planner goldens the C# port is scored against.
//
// The planner was written in JavaScript in oke-fleet (oke-fleet#9, branch
// feat/secret-plan) and moves here in C# (James's ruling on secret-provisioner#6;
// the language is his kit#176 answer). Every file this writes is the JS planner's
// OWN output for one scenario — not a drawing of it — so the C# test compares
// against a foreign artefact rather than against itself.
//
// Usage, with oke-fleet checked out at feat/secret-plan beside this repo:
//   node tools/plan-goldens.mjs ../oke-fleet
//
// If a regenerated golden changes, that is the news: read the diff before
// committing it.
import { writeFileSync, mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const okeFleet = resolve(process.argv[2] ?? '../oke-fleet');
const { planSecrets } = await import(pathToFileURL(join(okeFleet, 'scripts/plan-secrets.mjs')).href);
const out = resolve('backend/Balenthiran.SecretProvisioner.Tests/Fixtures/Plan');
mkdirSync(out, { recursive: true });

// secrets/around-the-world.json, verbatim — the five-key Secret the planner exists for.
const aroundTheWorld = {
  app: 'around-the-world',
  namespace: 'balenthiran',
  secretName: 'around-the-world-secrets',
  keys: [
    { key: 'ConnectionStrings__DefaultConnection', type: 'postgres-db', database: 'aroundtheworld', role: 'aroundtheworld' },
    { key: 'Jwt__Secret', type: 'random', bytes: 32 },
    { key: 'Admin__Key', type: 'random', bytes: 16 },
    { key: 'PhotoStorage__AccessKeyId', type: 'external' },
    { key: 'PhotoStorage__SecretAccessKey', type: 'external' },
  ],
};
const macroMetrics = { app: 'macro-metrics', namespace: 'balenthiran', secretName: 'macro-metrics-secrets', keys: [{ key: 'FRED_API_KEY', type: 'external' }] };
const balenthiran = { app: 'balenthiran', namespace: 'balenthiran', secretName: 'balenthiran-secrets', keys: [{ key: 'DATABASE_URL', type: 'postgres-db', database: 'balenthiran', role: 'balenthiran' }] };
const live = ['ConnectionStrings__DefaultConnection', 'Admin__Key', 'PhotoStorage__AccessKeyId', 'PhotoStorage__SecretAccessKey'];
const empty = (pg = { roles: [], databases: [] }) => ({ namespaces: ['balenthiran'], secrets: {}, postgres: pg });

const scenarios = {
  'empty-estate': [[aroundTheWorld, balenthiran, macroMetrics], empty()],
  'random-default-bytes': [[{ app: 'd', namespace: 'balenthiran', secretName: 'd-secrets', keys: [{ key: 'K', type: 'random' }] }], empty()],
  'five-key-secret-missing-one': [
    [aroundTheWorld],
    { namespaces: ['balenthiran'], secrets: { 'balenthiran/around-the-world-secrets': live }, postgres: { roles: ['aroundtheworld'], databases: ['aroundtheworld'] } },
  ],
  'every-key-present': [
    [aroundTheWorld],
    { namespaces: ['balenthiran'], secrets: { 'balenthiran/around-the-world-secrets': aroundTheWorld.keys.map((k) => k.key) }, postgres: { roles: ['aroundtheworld'], databases: ['aroundtheworld'] } },
  ],
  'role-exists-blocks': [[aroundTheWorld], empty({ roles: ['aroundtheworld'], databases: [] })],
  'database-without-role-blocks': [[aroundTheWorld], empty({ roles: [], databases: ['aroundtheworld'] })],
  'two-declarations-one-role': [
    [aroundTheWorld, { app: 'x', namespace: 'balenthiran', secretName: 'x-secrets', keys: [{ key: 'DB', type: 'postgres-db', database: 'xdb', role: 'aroundtheworld' }] }],
    empty(),
  ],
  'unmanaged-keys-named': [
    [macroMetrics],
    { namespaces: ['balenthiran'], secrets: { 'balenthiran/macro-metrics-secrets': ['FRED_API_KEY', 'Zeta', 'Alpha'] }, postgres: null },
  ],
  'present-key-role-missing-is-drift': [
    [aroundTheWorld],
    { namespaces: ['balenthiran'], secrets: { 'balenthiran/around-the-world-secrets': aroundTheWorld.keys.map((k) => k.key) }, postgres: { roles: [], databases: [] } },
  ],
  'namespace-not-enumerated': [[aroundTheWorld], { namespaces: ['other'], secrets: {}, postgres: { roles: [], databases: [] } }],
  'postgres-not-inspected': [[aroundTheWorld], { namespaces: ['balenthiran'], secrets: {}, postgres: null }],
  'postgres-absent-means-not-inspected': [[aroundTheWorld], { namespaces: ['balenthiran'], secrets: {} }],
  'unknown-type-stops': [[{ app: 'u', namespace: 'balenthiran', secretName: 'u-secrets', keys: [{ key: 'K', type: 'telepathy' }] }], empty()],
  'distinct-role-and-database': [
    [{ app: 'r', namespace: 'balenthiran', secretName: 'r-secrets', keys: [{ key: 'DB', type: 'postgres-db', database: 'the_db', role: 'the_role' }, { key: 'K', type: 'random', bytes: 20 }] }],
    empty(),
  ],
  'two-namespaces-mixed': [
    [aroundTheWorld, { app: 'k', namespace: 'kit', secretName: 'kit-secrets', keys: [{ key: 'S', type: 'random' }] }],
    { namespaces: ['balenthiran', 'kit'], secrets: { 'kit/kit-secrets': ['Other'], 'balenthiran/around-the-world-secrets': ['Jwt__Secret'] }, postgres: { roles: [], databases: [] } },
  ],
};

// Observations the planner must REFUSE, with the message it refuses them with.
const refusals = {
  'refuse-no-namespaces': [[aroundTheWorld], { secrets: {}, postgres: null }],
  'refuse-not-an-object': [[aroundTheWorld], []],
  'refuse-secret-keys-not-array': [[aroundTheWorld], { namespaces: ['balenthiran'], secrets: { 'balenthiran/x': 'K' } }],
  'refuse-bad-postgres': [[aroundTheWorld], { namespaces: ['balenthiran'], secrets: {}, postgres: { roles: [] } }],
};

let n = 0;
for (const [name, [declarations, observation]] of Object.entries(scenarios)) {
  const plan = planSecrets(structuredClone(declarations), structuredClone(observation));
  writeFileSync(join(out, `${name}.json`), JSON.stringify({ declarations, observation, plan }, null, 2) + '\n');
  n++;
}
for (const [name, [declarations, observation]] of Object.entries(refusals)) {
  let error = null;
  try {
    planSecrets(structuredClone(declarations), structuredClone(observation));
  } catch (e) {
    error = e.message;
  }
  if (error === null) throw new Error(`${name}: the planner accepted an observation this scenario expects it to refuse`);
  writeFileSync(join(out, `${name}.json`), JSON.stringify({ declarations, observation, error }, null, 2) + '\n');
  n++;
}
console.log(`wrote ${n} golden(s) to ${out}`);
