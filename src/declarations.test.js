// Run with: npm test  (i.e. bare `node --test`, which auto-discovers)
//
// ⚠️ NOT `node --test src/` — naming a directory makes Node resolve it as a
// module to execute and fail with MODULE_NOT_FOUND, which looks exactly like a
// broken suite.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { assertBranch, declarationBlobs, assertUsable, parseDeclarations, loadDeclarations, OKE_FLEET } from './declarations.js';

// `src/fixtures/contents-listing.json` is GitHub's ACTUAL answer, saved
// verbatim, not a drawing of one:
//
//   curl -sS "https://api.github.com/repos/jemmy8oy-northstar/oke-fleet/contents/secrets?ref=$SHA" \
//     > src/fixtures/contents-listing.json
//
// captured at oke-fleet `dev` = 1fa0825…, which held README.md and three
// declarations. This module's whole job is to read one specific API's response,
// so a hand-written listing would be testing my memory of that response — and
// it would go on passing after the response changed, which is the one thing a
// fixture on a seam must not do.
const LISTING_SHA = '1fa08255183319b82ea228db097534557f6564df';
const listing = () => JSON.parse(readFileSync(new URL('./fixtures/contents-listing.json', import.meta.url), 'utf8'));

// ── the branch is named literally ───────────────────────────────────────────

test('`dev` is the branch, and it is the reason the provisioner runs before the release', () => {
  assert.equal(OKE_FLEET.branch, 'dev');
});

test('HEAD is refused rather than resolved', () => {
  // Argo CD's `targetRevision: HEAD` means "the default branch" = `main`. If
  // this module ever resolved HEAD it would read the same branch Argo releases
  // from, the ordering that makes provisioning a precondition would be gone,
  // and nothing would look wrong until an app started without its Secret.
  assert.throws(() => assertBranch('HEAD'), /refusing to read declarations from `HEAD`/);
});

test('an empty or non-string branch is refused, so a missing config cannot become a default', () => {
  for (const bad of ['', undefined, null, 0]) assert.throws(() => assertBranch(bad), TypeError);
});

// ── the listing ─────────────────────────────────────────────────────────────

test('the three declarations are found and README.md is not', () => {
  const blobs = declarationBlobs(listing(), LISTING_SHA);
  assert.deepEqual(blobs.map((b) => b.name), ['around-the-world.json', 'balenthiran.json', 'macro-metrics.json']);
});

test('README.md is excluded by EXTENSION, not by name', () => {
  // Excluding the literal string "README.md" passes this suite today and breaks
  // the first time a NOTES.md or a CODEOWNERS appears beside the declarations.
  const withNotes = [...listing(), { name: 'NOTES.md', path: 'secrets/NOTES.md', type: 'file', download_url: `https://raw.githubusercontent.com/x/y/${LISTING_SHA}/secrets/NOTES.md` }];
  assert.deepEqual(declarationBlobs(withNotes, LISTING_SHA).map((b) => b.name), ['around-the-world.json', 'balenthiran.json', 'macro-metrics.json']);
});

test('a directory beside the declarations is simply not one of them', () => {
  // oke-fleet's validator is `readdirSync(secretsDir).filter(f => f.endsWith('.json'))`
  // — top level, by extension. A directory named `archive` is not a `.json`
  // and neither side looks inside it.
  const withDir = [...listing(), { name: 'archive', path: 'secrets/archive', type: 'dir', download_url: null }];
  assert.equal(declarationBlobs(withDir, LISTING_SHA).length, 3);
});

test('a `*.json` entry that is NOT a plain file stops the run — it is not silently skipped', () => {
  // The asymmetry matters. oke-fleet's validator reads with `readFileSync`,
  // which follows a symlink, so a symlinked declaration is one this estate's
  // CI has validated and passed. Skipping it here would mean provisioning
  // fewer apps than oke-fleet believes are declared, with nothing saying so —
  // which is the silent under-provisioning the whole module refuses. Anything
  // the two sides would read differently has to be loud.
  for (const type of ['symlink', 'submodule', 'dir']) {
    const entry = { name: 'link.json', path: 'secrets/link.json', type, download_url: `https://raw.githubusercontent.com/x/y/${LISTING_SHA}/secrets/link.json` };
    assert.throws(() => declarationBlobs([...listing(), entry], LISTING_SHA), new RegExp(`secrets/link\\.json is a "${type}"`));
  }
});

test('the order is the validator\'s order, whatever order GitHub returns', () => {
  const shuffled = [...listing()].reverse();
  assert.deepEqual(
    declarationBlobs(shuffled, LISTING_SHA).map((b) => b.name),
    declarationBlobs(listing(), LISTING_SHA).map((b) => b.name),
  );
});

test('a listing that is not an array is a FAILED read, never an empty directory', () => {
  // The contents API answers a missing path with `{"message":"Not Found"}` —
  // valid JSON, and `(x ?? []).filter(...)` would turn it into zero
  // declarations and a clean reconcile that provisioned nothing.
  assert.throws(() => declarationBlobs({ message: 'Not Found' }, LISTING_SHA), /not an array/);
  assert.throws(() => declarationBlobs({ message: 'Not Found' }, LISTING_SHA), /"Not Found"/);
  assert.throws(() => declarationBlobs(null, LISTING_SHA), /not an array/);
});

test('zero declarations is a refusal — a wrong ref and an empty directory look identical from here', () => {
  assert.throws(() => declarationBlobs([listing()[0]], LISTING_SHA), /no \.json declarations/);
  assert.throws(() => declarationBlobs([], LISTING_SHA), /no \.json declarations/);
});

test('a download URL not pinned to the resolved commit is refused', () => {
  // This is what keeps the read atomic. A `download_url` on the branch rather
  // than the SHA means a push landing mid-read assembles a set of declarations
  // that never existed as a commit — and every file would still parse.
  const drifted = listing().map((e) => ({ ...e, download_url: e.download_url?.replace(LISTING_SHA, 'dev') }));
  assert.throws(() => declarationBlobs(drifted, LISTING_SHA), /not pinned to/);
});

test('a file with no download URL is refused rather than skipped', () => {
  const broken = listing().map((e) => (e.name === 'balenthiran.json' ? { ...e, download_url: null } : e));
  assert.throws(() => declarationBlobs(broken, LISTING_SHA), /no download URL/);
});

// ── the shape ───────────────────────────────────────────────────────────────

const usable = () => ({ app: 'a', namespace: 'balenthiran', secretName: 'a-secrets', keys: [{ key: 'K', type: 'random' }] });

test('the usable shape is accepted', () => {
  assert.deepEqual(assertUsable(usable(), 'a.json'), usable());
});

test('every refusal names the file it came from', () => {
  // A reconcile reads several files and the message is all anyone gets — "keys
  // must be a non-empty array" without a filename is a hunt through a public
  // repo for which of three it meant.
  assert.throws(() => assertUsable({ ...usable(), keys: [] }, 'macro-metrics.json'), /^Error: macro-metrics\.json: /);
});

test('an array is not a declaration, even though it is an object', () => {
  assert.throws(() => assertUsable([usable()], 'a.json'), /must be a JSON object/);
  assert.throws(() => assertUsable(null, 'a.json'), /must be a JSON object/);
});

test('a missing namespace or secretName is refused — both are addresses, not defaults', () => {
  assert.throws(() => assertUsable({ ...usable(), namespace: '' }, 'a.json'), /namespace/);
  assert.throws(() => assertUsable({ ...usable(), secretName: undefined }, 'a.json'), /secretName/);
});

test('a key entry missing `key` or `type` is refused, and the index is named', () => {
  const twoKeys = (second) => ({ ...usable(), keys: [{ key: 'K', type: 'random' }, second] });
  assert.throws(() => assertUsable(twoKeys({ type: 'random' }), 'a.json'), /keys\[1\]\.key/);
  assert.throws(() => assertUsable(twoKeys({ key: 'J' }), 'a.json'), /keys\[1\]\.type/);
  assert.throws(() => assertUsable(twoKeys('J'), 'a.json'), /keys\[1\] must be an object/);
});

test('an UNKNOWN type is accepted here — the schema lives in oke-fleet and is not copied', () => {
  // Deliberate. `oke-fleet:scripts/validate-secrets.mjs` owns the list of valid
  // types and runs in that repo's CI on every pull request into `dev`.
  // Re-implementing it here would give the estate two definitions that drift
  // apart in silence, with the copy nobody edits deciding what gets created.
  // The executor already refuses a step whose declaration disagrees with it.
  assert.doesNotThrow(() => assertUsable({ ...usable(), keys: [{ key: 'K', type: 'not-a-real-type' }] }, 'a.json'));
});

test('a non-JSON body names what it actually got', () => {
  // raw.githubusercontent answers a missing blob with the plain text
  // `404: Not Found`, which arrives here as a parse error. Quoting the start of
  // the body is what separates "the file is corrupt" from "the file is gone".
  assert.throws(() => parseDeclarations([{ name: 'secrets/a.json', body: '404: Not Found' }]), /not JSON/);
  assert.throws(() => parseDeclarations([{ name: 'secrets/a.json', body: '404: Not Found' }]), /"404: Not Found"/);
});

// ── the whole load ──────────────────────────────────────────────────────────

const declarationBody = (app) => JSON.stringify({ app, namespace: 'balenthiran', secretName: `${app}-secrets`, keys: [{ key: 'K', type: 'random' }] });

/** A fetch that answers this estate's real URLs and records what was asked. */
function stubFetch({ sha = LISTING_SHA, fail = () => null } = {}) {
  const calls = [];
  const impl = async (url, init) => {
    calls.push(url);
    const forced = fail(url);
    if (forced) return { ok: false, status: forced, headers: { get: () => '0' }, text: async () => '' };
    const reply = (body) => ({ ok: true, status: 200, headers: { get: () => '59' }, text: async () => body });
    if (url.includes('/commits/')) {
      assert.equal(init.headers.accept, 'application/vnd.github.sha', 'one request must resolve the branch to a bare SHA, not a commit object');
      return reply(`${sha}\n`);
    }
    if (url.includes('/contents/')) return reply(readFileSync(new URL('./fixtures/contents-listing.json', import.meta.url), 'utf8'));
    const app = url.split('/').pop().replace('.json', '');
    return reply(declarationBody(app));
  };
  return { impl, calls };
}

test('the load resolves the branch once, then reads everything at that commit', async () => {
  const { impl, calls } = stubFetch();
  const result = await loadDeclarations({ fetchImpl: impl });

  assert.equal(result.sha, LISTING_SHA);
  assert.equal(result.branch, 'dev');
  assert.equal(result.declarations.length, 3);

  assert.equal(calls.filter((u) => u.includes('/commits/')).length, 1, 'the branch must be resolved once, not once per file');
  // Every read after the resolve carries the SHA. This is the property, not the
  // call count: listing at `dev` and then fetching at `dev` is two reads of a
  // moving branch, and a push between them yields a set that never existed.
  for (const url of calls.slice(1)) assert.ok(url.includes(LISTING_SHA), `${url} was not read at the resolved commit`);
});

test('only two requests touch the API host — the rest go to raw', async () => {
  // The unauthenticated GitHub API allows 60 requests an hour PER IP, shared
  // with everything else this pod does. raw.githubusercontent does not spend
  // that budget, so a reconcile costs 2 requests however many apps exist —
  // measured, and the reason the one-contents-call-per-file design was not used.
  const { impl, calls } = stubFetch();
  await loadDeclarations({ fetchImpl: impl });
  assert.equal(calls.filter((u) => u.startsWith('https://api.github.com/')).length, 2);
  assert.equal(calls.filter((u) => u.startsWith('https://raw.githubusercontent.com/')).length, 3);
});

test('ONE file failing fails the whole load — a partial read is not a smaller estate', async () => {
  // The silent failure this module exists to prevent. A create-only
  // provisioner handed two of three declarations damages nothing: it creates
  // two Secrets, reports success, and leaves one app unprovisioned with
  // nothing anywhere saying so.
  const { impl } = stubFetch({ fail: (url) => (url.endsWith('macro-metrics.json') ? 500 : null) });
  await assert.rejects(loadDeclarations({ fetchImpl: impl }), /macro-metrics\.json answered 500/);
});

test('a rate-limited request says so, and says how much quota is left', async () => {
  // 403-because-rate-limited fixes itself in under an hour; 403-because-
  // something-else does not, and neither does a 404 on a wrong ref. The status
  // alone cannot tell them apart.
  const { impl } = stubFetch({ fail: (url) => (url.includes('/commits/') ? 403 : null) });
  await assert.rejects(loadDeclarations({ fetchImpl: impl }), /answered 403, 0 API requests left this hour/);
});

test('a branch that resolves to something that is not a SHA stops the load', async () => {
  // A redirect, an HTML error page, or a 200 with an empty body all reach here
  // as a "successful" resolve. Concatenating that into the next URL would
  // produce a 404 three calls later, blamed on the wrong thing.
  const { impl } = stubFetch({ sha: '<!DOCTYPE html>' });
  await assert.rejects(loadDeclarations({ fetchImpl: impl }), /which is not a commit SHA/);
});

test('no declaration is invented: what comes back is what was fetched', async () => {
  const { impl } = stubFetch();
  const { declarations } = await loadDeclarations({ fetchImpl: impl });
  assert.deepEqual(declarations.map((d) => d.app), ['around-the-world', 'balenthiran', 'macro-metrics']);
});
