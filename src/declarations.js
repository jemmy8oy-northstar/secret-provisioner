// The declarations loader: reads `oke-fleet/secrets/*.json` over HTTPS, at one
// commit, and refuses to hand back a partial answer. Slice 4 of
// claude-code-bot#97.
//
// James's shape, on claude-code-bot#97: the declarations stay in `oke-fleet` and
// are read at reconcile time rather than baked into this image, so adding an app
// is a data change in a config repo and not a container rebuild. No credential
// is involved, because `oke-fleet` is public.
//
// WHY `dev`, LITERALLY, AND WHY `HEAD` IS REFUSED
// `apps-root/fleet-generator.yaml` uses `targetRevision: HEAD`, and in Argo CD
// `HEAD` means the repository's DEFAULT branch — which is `main`. So Argo
// releases from `main`, and this provisioner reads `dev`: that one difference is
// what makes provisioning a PRECONDITION of release rather than something that
// happens at the same moment. Point it at `main` and the ordering is gone while
// everything still appears to work.
//
// `HEAD` is therefore refused as a branch name here rather than resolved. It is
// a setting whose meaning is "whatever the default branch is", so a repository
// whose default branch was renamed would silently re-converge on `main` and the
// only symptom would be an app released before its Secret existed. Naming `dev`
// literally makes that failure loud [[the message names the layer]].
//
// A PARTIAL READ IS NOT A SMALLER ESTATE
// The observer's hazard was destructive and this one is the opposite: silent.
// A create-only provisioner given four of five declarations does not damage
// anything — it creates four Secrets, reports success, and leaves one app
// unprovisioned with nothing anywhere saying so. Nobody would find that until
// the app failed to start. So every failure here throws, and there is no code
// path that returns fewer declarations than the listing named.
//
// ONE COMMIT, NOT ONE BRANCH
// `dev` is resolved to a commit SHA ONCE, and the listing and every file are
// read at that SHA. Reading `secrets/` at `dev` and then fetching each file at
// `dev` is two reads of a moving branch, and a push landing between them yields
// a set of declarations that never existed as a commit. This also gives the
// reconcile report something exact to name — "provisioned from oke-fleet@<sha>"
// — which is the only audit trail that survives the fact that no value may ever
// be logged.
//
// NOTHING HERE IS A SECRET, AND THAT IS WHY THIS FILE IS SIMPLE
// A declaration states intent — namespace, Secret name, key, and how the value
// should come to exist. It never holds a value, so unlike `observe.js` and
// `execute.js` this module needs no redaction discipline at all. Errors quote
// the URL and the status freely, which is what makes a failure here diagnosable.

/** Where the declarations live. Overridable only so tests can name a fixture. */
export const OKE_FLEET = Object.freeze({
  owner: 'jemmy8oy-northstar',
  repo: 'oke-fleet',
  branch: 'dev',
  dir: 'secrets',
});

// `readdirSync(secretsDir).filter((f) => f.endsWith('.json')).sort()` — that is
// the literal line in `oke-fleet:scripts/validate-secrets.mjs`, and this is the
// other side of the same seam. It must enumerate EXACTLY what the validator
// validates: `.json`, top level only, sorted. A file the validator never saw is
// a file CI never checked, and provisioning from an unchecked declaration is how
// a typo reaches a `CREATE DATABASE`.
const DECLARATION_EXT = '.json';

/**
 * The branch to read. Refuses anything that does not name one literally.
 *
 * @param {string} branch
 * @returns {string} the same branch
 */
export function assertBranch(branch) {
  if (typeof branch !== 'string' || branch === '') {
    throw new TypeError(`refusing to read declarations from ${JSON.stringify(branch)} — the branch must be named literally`);
  }
  if (branch === 'HEAD') {
    throw new Error(
      'refusing to read declarations from `HEAD`. HEAD is a setting meaning "the default branch", which here is `main` — the branch Argo CD releases from. '
      + 'The provisioner reads `dev` so that provisioning happens BEFORE release; resolving HEAD would silently collapse that ordering.',
    );
  }
  return branch;
}

/**
 * The `*.json` blobs in a GitHub contents listing, in the validator's order.
 *
 * @param {unknown} listing the contents API response for the directory
 * @param {string} sha the commit the listing was requested at
 * @returns {{name: string, path: string, url: string}[]}
 */
export function declarationBlobs(listing, sha) {
  // The contents API answers a MISSING path with `{"message": "Not Found"}` and
  // a single file with an object — both of which are valid JSON, and both of
  // which `.filter` would turn into "no declarations" if this were an array
  // check written as `(listing ?? []).filter(...)`. An answer that is not a
  // directory listing is not an empty directory [[empty-means-two-things]].
  if (!Array.isArray(listing)) {
    const message = listing && typeof listing === 'object' && 'message' in listing ? ` — GitHub said ${JSON.stringify(listing.message)}` : '';
    throw new Error(`the contents listing for ${OKE_FLEET.dir} is not an array${message}. This is not an empty directory; it is a failed read`);
  }

  const named = listing.filter((entry) => typeof entry?.name === 'string' && entry.name.endsWith(DECLARATION_EXT));

  // A `*.json` entry that is not a plain file is REFUSED, not skipped — and
  // that asymmetry is the point. The contents API also returns `dir`,
  // `symlink` and `submodule`. oke-fleet's validator uses `readFileSync`,
  // which FOLLOWS a symlink, so a symlinked declaration is one this estate's
  // CI has validated and passed. Dropping it here would mean the provisioner
  // quietly ignoring a declaration oke-fleet considers live — the silent
  // under-provisioning this whole module refuses. Anything the two sides would
  // read differently has to stop the run instead.
  for (const entry of named) {
    if (entry.type !== 'file') {
      throw new Error(`${entry.path} is a ${JSON.stringify(entry.type)}, not a file. oke-fleet's validator reads it with readFileSync and this reads it over HTTPS, so the two would not agree about what it contains`);
    }
  }

  const blobs = named
    .map((entry) => ({ name: entry.name, path: entry.path, url: entry.download_url }))
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));

  if (blobs.length === 0) {
    // Deliberately an error. A create-only provisioner with nothing to create
    // does no work either way, so refusing costs nothing — while succeeding
    // would make "the ref is wrong", "the directory moved" and "he deleted
    // every declaration" produce one identical clean reconcile.
    throw new Error(`no ${DECLARATION_EXT} declarations under ${OKE_FLEET.dir}/ at ${sha}. A wrong ref and an empty directory look the same from here, so this is a refusal rather than an empty run`);
  }

  for (const blob of blobs) {
    if (typeof blob.url !== 'string' || blob.url === '') {
      throw new Error(`${blob.path} has no download URL in the listing — nothing can be fetched for it`);
    }
    // The listing was requested at a SHA, so every download_url GitHub returns
    // is pinned to that SHA. If one is not, the listing did not come from the
    // commit we asked for, and the set about to be assembled would span two
    // trees. Checking is one line; the failure it prevents is a declaration set
    // that never existed as a commit.
    if (!blob.url.includes(sha)) {
      throw new Error(`${blob.path}'s download URL is not pinned to ${sha} (${blob.url}) — the listing and the files would come from different commits`);
    }
  }

  return blobs;
}

/**
 * The shape this provisioner itself dereferences. Deliberately NOT the schema.
 *
 * `oke-fleet:scripts/validate-secrets.mjs` is the schema, it runs in oke-fleet's
 * CI on every pull request into `dev`, and re-implementing it here would give
 * this estate two definitions of a valid declaration that drift apart in silence
 * — with the copy nobody edits being the one that decides what gets created.
 *
 * What is checked here is only what would otherwise fail HALF WAY THROUGH a
 * reconcile: a create-only run that has already made three Secrets and then
 * throws on a missing field leaves the cluster in a state nothing planned.
 *
 * @param {unknown} value
 * @param {string} name the file it came from, so the error names it
 */
export function assertUsable(value, name) {
  const bad = (why) => { throw new Error(`${name}: ${why}`); };
  if (value === null || typeof value !== 'object' || Array.isArray(value)) bad('a declaration must be a JSON object');
  for (const field of ['namespace', 'secretName']) {
    if (typeof value[field] !== 'string' || value[field] === '') bad(`${field} must be a non-empty string`);
  }
  if (!Array.isArray(value.keys) || value.keys.length === 0) bad('keys must be a non-empty array');
  for (const [i, entry] of value.keys.entries()) {
    if (entry === null || typeof entry !== 'object' || Array.isArray(entry)) bad(`keys[${i}] must be an object`);
    if (typeof entry.key !== 'string' || entry.key === '') bad(`keys[${i}].key must be a non-empty string`);
    if (typeof entry.type !== 'string' || entry.type === '') bad(`keys[${i}].type must be a non-empty string`);
  }
  return value;
}

/**
 * Parse the fetched files into the array the planner and executor consume.
 *
 * @param {{name: string, body: string}[]} files
 */
export function parseDeclarations(files) {
  return files.map(({ name, body }) => {
    let parsed;
    try {
      parsed = JSON.parse(body);
    } catch (error) {
      // raw.githubusercontent answers a missing blob with the plain text
      // `404: Not Found`, which reaches here as a parse error. Quoting the
      // start of the body is what tells those two apart in a log.
      throw new Error(`${name}: not JSON (${error.message}) — the response began ${JSON.stringify(String(body).slice(0, 40))}`);
    }
    return assertUsable(parsed, name);
  });
}

/** A fetch whose non-2xx answers throw with the status and the URL. */
async function getText(fetchImpl, url, accept) {
  const response = await fetchImpl(url, { headers: { accept, 'user-agent': 'secret-provisioner' } });
  if (!response.ok) {
    // 403 with the rate-limit headers, 404 for a wrong ref and 500 from GitHub
    // are three different problems and one of them fixes itself. The status and
    // the remaining quota are what distinguish them, so both go in the message.
    const remaining = response.headers?.get?.('x-ratelimit-remaining');
    const quota = remaining === null || remaining === undefined ? '' : `, ${remaining} API requests left this hour`;
    throw new Error(`GET ${url} answered ${response.status}${quota}`);
  }
  return response.text();
}

/**
 * Load every declaration, at one commit, or throw.
 *
 * Two calls land on `api.github.com` (resolve the branch, list the directory)
 * and the rest on `raw.githubusercontent.com`, which does not spend the
 * unauthenticated 60-requests-per-hour API budget. That budget is why the
 * cheaper-looking design — one contents call per file — is not used: it would
 * make the cost of a reconcile grow with the number of apps, on a quota that is
 * per IP and shared with everything else this pod does.
 *
 * @param {object} [options]
 * @param {typeof fetch} [options.fetchImpl]
 * @returns {Promise<{owner: string, repo: string, branch: string, sha: string, declarations: object[]}>}
 */
export async function loadDeclarations({ fetchImpl = globalThis.fetch, source = OKE_FLEET } = {}) {
  const { owner, repo, dir } = source;
  const branch = assertBranch(source.branch);
  const api = `https://api.github.com/repos/${owner}/${repo}`;

  // `Accept: application/vnd.github.sha` makes this return the bare commit SHA
  // as the whole body — no JSON, no commit object, and one request.
  const sha = (await getText(fetchImpl, `${api}/commits/${branch}`, 'application/vnd.github.sha')).trim();
  if (!/^[0-9a-f]{40}$/.test(sha)) {
    throw new Error(`resolving ${branch} gave ${JSON.stringify(sha.slice(0, 80))}, which is not a commit SHA`);
  }

  const listing = JSON.parse(await getText(fetchImpl, `${api}/contents/${dir}?ref=${sha}`, 'application/vnd.github+json'));
  const blobs = declarationBlobs(listing, sha);

  // All of them or none. `Promise.all` rejects on the first failure, which is
  // the behaviour wanted — but the reason it is wanted is worth stating,
  // because `allSettled` plus a filter is the natural-looking alternative and
  // it is exactly the partial read this module exists to refuse.
  const files = await Promise.all(
    blobs.map(async (blob) => ({ name: blob.path, body: await getText(fetchImpl, blob.url, 'text/plain') })),
  );

  return { owner, repo, branch, sha, declarations: parseDeclarations(files) };
}
