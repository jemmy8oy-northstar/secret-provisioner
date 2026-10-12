// The observer: turns what the Kubernetes API and Postgres actually return into
// the observation the planner consumes.
//
// Everything here is PURE. The clients live at the edge and hand their responses
// in; these functions have no I/O, so the two questions that matter — "does it
// ever touch a value" and "does it ever claim to have looked somewhere it
// didn't" — are answerable by a test rather than by an audit of a live cluster.
//
// THE CONTRACT WITH THE PLANNER, AND WHY IT IS THE POINT
// oke-fleet's planner refuses to plan against a namespace the observation does
// not list, because "this namespace holds no Secrets" and "I never listed this
// namespace" are the same empty object, and a failed lookup read as the first
// plans a CREATE for a Secret that already exists — which deletes every key not
// in the plan. `around-the-world-secrets` holds five keys; two of them are
// Jwt__Secret and Admin__Key, so the visible symptom is every guest signed out
// mid-party rather than an error.
//
// That refusal is only worth anything if the observer never lies about what it
// enumerated. A namespace whose listing FAILED must not appear in `namespaces`,
// even though the natural way to write the loop puts it there. That is the seam,
// and it is pinned from both sides: the planner refuses, and `observation()`
// below takes only namespaces that succeeded.
//
// NO VALUE EVER LEAVES THIS MODULE
// A Kubernetes Secret arrives with its values in `data`, base64-encoded, sitting
// right next to the key names we want. Reading `Object.keys(data)` and reading
// `data` are one character apart. So the value is dropped here, at the earliest
// possible point, and nothing downstream is ever given the chance to log it.

// Postgres ships these and they are not ours. Without filtering them out,
// `template1` reads as a database name that is already taken, and every
// `pg_`-prefixed role looks like a collision with an app.
const SYSTEM_DATABASES = new Set(['postgres', 'template0', 'template1']);
const SYSTEM_ROLE_PREFIX = 'pg_';

/**
 * Key names present in one Kubernetes Secret.
 *
 * @param {object} secret a v1.Secret as the API returns it
 * @returns {string[]} sorted key names — never a value
 */
export function keysOf(secret) {
  // `data` and `stringData` are both possible; a Secret written by kubectl from
  // a literal has `data`, one written from a manifest may have either. Reading
  // only `data` would report a Secret as missing a key it plainly has, and the
  // planner would then plan to patch a key that is already there.
  const names = new Set([...Object.keys(secret?.data ?? {}), ...Object.keys(secret?.stringData ?? {})]);
  return [...names].sort();
}

/**
 * Index a namespace's Secret listing by `<namespace>/<name>`.
 *
 * @param {string} namespace
 * @param {object} listing a v1.SecretList — `{ items: [...] }`
 * @returns {Record<string, string[]>}
 */
export function indexSecrets(namespace, listing) {
  const out = {};
  for (const secret of listing?.items ?? []) {
    const name = secret?.metadata?.name;
    // A Secret with no name cannot be addressed and cannot be what a
    // declaration means. Skipping it is right; silently skipping a NAMESPACE
    // would not be, which is the distinction this whole module turns on.
    if (typeof name !== 'string' || name === '') continue;
    out[`${namespace}/${name}`] = keysOf(secret);
  }
  return out;
}

/**
 * Roles and databases that belong to us, from the two catalogue queries.
 *
 * @param {{rows: {rolname: string}[]}} roleRows   SELECT rolname FROM pg_roles
 * @param {{rows: {datname: string}[]}} dbRows     SELECT datname FROM pg_database
 */
export function indexPostgres(roleRows, dbRows) {
  const roles = (roleRows?.rows ?? [])
    .map((r) => r.rolname)
    .filter((n) => typeof n === 'string' && !n.startsWith(SYSTEM_ROLE_PREFIX) && !SYSTEM_DATABASES.has(n))
    .sort();

  const databases = (dbRows?.rows ?? [])
    .map((r) => r.datname)
    .filter((n) => typeof n === 'string' && !SYSTEM_DATABASES.has(n))
    .sort();

  return { roles, databases };
}

/**
 * Assemble the observation the planner takes.
 *
 * @param {{namespace: string, listing?: object, error?: unknown}[]} results
 *   One entry per namespace we ATTEMPTED. An entry carrying an `error` is a
 *   namespace we failed to read: it contributes nothing, and above all its name
 *   does NOT go into `namespaces`.
 * @param {{roles: string[], databases: string[]} | null} postgres
 *   `null` means the server was not inspected — which is not the same as empty,
 *   and the planner treats it differently.
 */
export function observation(results, postgres) {
  const namespaces = [];
  const secrets = {};
  const failures = [];

  for (const result of results) {
    if (result.error !== undefined && result.error !== null) {
      // Deliberately NOT added to `namespaces`. The planner will report every
      // key in it as undecidable, which is loud and correct; adding it here
      // would make a failed listing indistinguishable from an empty namespace
      // and turn a transient API error into a destructive plan.
      failures.push({ namespace: result.namespace, error: String(result.error?.message ?? result.error) });
      continue;
    }
    namespaces.push(result.namespace);
    Object.assign(secrets, indexSecrets(result.namespace, result.listing));
  }

  return { namespaces: [...new Set(namespaces)].sort(), secrets, postgres, failures };
}
