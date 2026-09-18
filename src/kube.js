// The Kubernetes edge: the only file here that opens a socket to a cluster.
// Slice 6 of claude-code-bot#97.
//
// Everything above this file is pure — `observe.js` turns responses into an
// observation, oke-fleet's planner turns that into a plan, `execute.js` turns
// the plan into calls on an injected `effects` object. This is what implements
// `effects` for real, and what feeds `observe.js` its input. It is therefore the
// file where a mistake is a live cluster rather than a failing test.
//
// IT ADDS NO DEPENDENCIES, AND THAT IS A DECISION
// The obvious move is `@kubernetes/client-node`. It is not used, because what
// is needed here is four requests against three URLs, and the library brings a
// large transitive tree into a container whose entire job is handling
// credentials. `node:https` does it in the file you are reading. The cost is
// that the request shapes below are ours to get right, which is what the tests
// and `mutants/kube.json` are for. If this ever grows to need watches,
// informers, or the dozen other API groups, that trade flips — take the library
// then, in a PR that says so.
//
// The second effect of adding no dependency: `npm test` still runs with no
// `npm ci`, so `.github/workflows/ci.yml` is untouched and this is not a
// platform change (James, claude-code-bot#83). The Postgres client cannot do
// that — nobody should hand-roll that wire protocol — so it is a separate
// slice, and it arrives with its dependency and with the isolated workflow PR
// that `npm ci` requires.
//
// THE TRANSPORT IS INJECTED, SO EVERY REQUEST SHAPE IS DECIDED BY A TEST
// `kubeClient` takes a `transport`. The real one is `httpsTransport` at the
// bottom of this file and is the only part not covered by a unit test, which is
// deliberate: it is reduced to "do what these options say" so that everything
// with a decision in it — the method, the path, the content type, the
// preconditions — is above the seam and asserted.
//
// TWO RULES ABOUT WHAT MAY BE QUOTED, AND THEY ARE OPPOSITES
//   - A **non-2xx** body MAY be quoted into an error. It is a `v1.Status`, whose
//     message is the only useful thing anyone gets when a call fails, and it
//     contains no Secret values.
//   - A **2xx** body may NEVER be. `GET /secrets/<name>` answers 200 with every
//     value in that Secret sitting in `data`, and this module reads one such
//     response on the patch path. Quoting it into an error message would put a
//     password into `execute`'s report, past both of that module's redactions,
//     because those only know about values this run generated.
//   - The service-account **token** is redacted out of everything unconditionally.
//     Node puts request options into some socket-level error messages, and the
//     token is in a header.
import { readFileSync } from 'node:fs';
import https from 'node:https';
import { keysOf } from './observe.js';

/** Where the kubelet mounts the pod's identity. Not configurable; it is a kubelet contract. */
export const SERVICE_ACCOUNT_DIR = '/var/run/secrets/kubernetes.io/serviceaccount';

/** A hung API call must not hold `/reconcile` open forever. */
export const DEFAULT_TIMEOUT_MS = 30_000;

/**
 * Remove the service-account token from a string.
 *
 * Same shape as `execute.js`'s `redactValues`, and for the same reason: the
 * thing being protected and the thing being printed are one property access
 * apart.
 */
export function redactToken(text, token) {
  const out = String(text);
  if (typeof token !== 'string' || token.length === 0) return out;
  return out.split(token).join('<redacted>');
}

/**
 * The in-cluster connection details, from the kubelet's mount and the env.
 *
 * Every field is required and nothing is defaulted. A default here is a client
 * that silently talks to the wrong cluster, or to no cluster, and reports the
 * result as an observation — and an observation that is wrong about what it
 * enumerated is the one failure this whole codebase is arranged around.
 *
 * @param {{env?: object, read?: (path: string) => string}} deps
 */
export function inClusterConfig({ env = process.env, read = (p) => readFileSync(p, 'utf8') } = {}) {
  const host = env.KUBERNETES_SERVICE_HOST;
  const port = env.KUBERNETES_SERVICE_PORT_HTTPS ?? env.KUBERNETES_SERVICE_PORT ?? '443';

  if (typeof host !== 'string' || host === '') {
    throw new Error('KUBERNETES_SERVICE_HOST is not set — this process is not running in a cluster, and there is no sensible default for which cluster to provision');
  }
  const portNumber = Number(port);
  if (!Number.isInteger(portNumber) || portNumber < 1 || portNumber > 65535) {
    throw new Error(`KUBERNETES_SERVICE_PORT must be an integer from 1 to 65535, got ${JSON.stringify(port)}`);
  }

  let token;
  let ca;
  try {
    token = read(`${SERVICE_ACCOUNT_DIR}/token`).trim();
    ca = read(`${SERVICE_ACCOUNT_DIR}/ca.crt`);
  } catch (error) {
    throw new Error(`cannot read the service account at ${SERVICE_ACCOUNT_DIR}: ${error?.message ?? error}. The Deployment must not set automountServiceAccountToken: false`);
  }
  if (token === '') {
    throw new Error(`the service account token at ${SERVICE_ACCOUNT_DIR}/token is empty`);
  }
  // Refused rather than passed to `https.request`, which accepts an empty `ca`
  // by falling back to Node's PUBLIC root store — so an empty file would turn
  // "verify against the cluster CA" into "verify against the internet", which
  // fails in a way that looks like a network problem rather than like the
  // authentication decision it actually is.
  if (String(ca).trim() === '') {
    throw new Error(`the cluster CA at ${SERVICE_ACCOUNT_DIR}/ca.crt is empty — refusing to fall back to the public root store`);
  }

  return { host, port: portNumber, token, ca };
}

/**
 * Was this a success?
 *
 * Written once so that no call site can decide it differently. `201` for a
 * create and `200` for a read are both fine; `202` is not reachable on these
 * routes but costs nothing to allow.
 */
const ok = (status) => status >= 200 && status < 300;

/** Parse a body that should be JSON, without letting a proxy's HTML take the process down. */
function parseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

/**
 * The error for a non-2xx response.
 *
 * Quotes the body, which is a `v1.Status` — see the header. `reason` is carried
 * as a property so a caller can branch on `AlreadyExists` or `Conflict` without
 * matching on English.
 */
function apiError(what, status, text, token) {
  const status_ = parseJson(text);
  const message = status_?.message ?? String(text).slice(0, 500);
  const error = new Error(redactToken(`${what}: the Kubernetes API answered ${status} — ${message}`, token));
  error.status = status;
  error.reason = status_?.reason ?? null;
  return error;
}

/**
 * A client for the four things this provisioner does to a cluster.
 *
 * @param {{config: object, transport?: Function}} deps
 *   `transport({method, path, headers, body})` resolves `{status, text}`.
 */
export function kubeClient({ config, transport = httpsTransport(config) }) {
  const { token } = config;

  async function call(what, { method, path, contentType, body }) {
    const headers = {
      authorization: `Bearer ${token}`,
      accept: 'application/json',
    };
    const payload = body === undefined ? undefined : JSON.stringify(body);
    if (payload !== undefined) {
      headers['content-type'] = contentType;
      // Byte length, not character length: a UTF-8 name makes the two differ,
      // and a short content-length truncates the body into invalid JSON that
      // the API rejects with a parse error nobody would connect to this line.
      headers['content-length'] = String(Buffer.byteLength(payload));
    }

    let response;
    try {
      response = await transport({ method, path, headers, body: payload });
    } catch (error) {
      // A transport-level failure — DNS, TLS, timeout. Node has been known to
      // attach the request options, and the options carry the header above.
      throw new Error(redactToken(`${what}: ${error?.message ?? error}`, token));
    }
    if (!ok(response.status)) throw apiError(what, response.status, response.text, token);
    return response;
  }

  /**
   * The Secrets in one namespace, as `v1.SecretList`.
   *
   * The values come back in this response and are dropped by `observe.js`'s
   * `indexSecrets` at the first point anything reads it. They are not dropped
   * here, because doing it in two places means the one that matters can be
   * removed while the other still looks like it is doing the job.
   */
  async function listSecrets(namespace) {
    const { text } = await call(`listing Secrets in ${namespace}`, {
      method: 'GET',
      path: `/api/v1/namespaces/${encodeURIComponent(namespace)}/secrets`,
    });
    const listing = parseJson(text);
    if (listing === null || !Array.isArray(listing.items)) {
      // NOT an empty listing. A response we cannot parse is a namespace we did
      // not read, and the difference between those two is the difference
      // between a correct plan and one that deletes four keys out of
      // `around-the-world-secrets`.
      throw new Error(`listing Secrets in ${namespace}: the API answered 200 with something that is not a v1.SecretList`);
    }
    return listing;
  }

  /**
   * One attempt per namespace, in the shape `observation()` takes.
   *
   * ⚠️ THE ENTIRE CONTRACT OF THIS FUNCTION IS THE `catch`. A namespace whose
   * listing failed comes back as `{namespace, error}` with **no `listing`
   * property at all**, so `observation()` keeps it out of `namespaces` and the
   * planner refuses to decide anything in it. Turning the failure into
   * `{items: []}` — the natural, tidy-looking thing to write — makes a
   * transient 503 indistinguishable from an empty namespace, and the planner
   * then plans a create for a Secret that already exists. That create-only
   * write is refused by the server, but a `patch` of a *different* key in the
   * same Secret is not, and the plan is built on a lie either way.
   *
   * Nor may a failure be dropped from the array: it would still keep the
   * namespace out of `namespaces`, so nothing destructive follows, but
   * `observation().failures` would be empty and the run would report a list of
   * undecidable keys with no stated reason.
   *
   * Sequential, not `Promise.all`: this runs against a handful of namespaces on
   * a human-triggered endpoint, and a serial loop cannot produce a burst that
   * the API server priority-and-fairness queue sheds — which would arrive here
   * as exactly the spurious per-namespace failure described above.
   */
  async function observeNamespaces(namespaces) {
    const results = [];
    for (const namespace of namespaces) {
      try {
        results.push({ namespace, listing: await listSecrets(namespace) });
      } catch (error) {
        results.push({ namespace, error });
      }
    }
    return results;
  }

  /**
   * Create a Secret. `effects.createSecret`.
   *
   * **POST, and it must stay POST.** A PUT to the collection is not a thing, but
   * a PUT to the item path is an unconditional replace, and an apply-patch is a
   * replace with better manners: either one silently discards every key in an
   * existing Secret that is not in `data`. The plan was made against an
   * observation taken moments earlier, so "does it exist" cannot be answered
   * here without a race. The 409 the API server returns is not an inconvenience
   * to be worked around — it is the entire create-only guarantee, enforced by
   * the only party that can enforce it.
   *
   * Values go in `stringData`, so nothing in this repo ever base64-encodes a
   * secret. The API server does the encoding; we never hold the encoded form,
   * and there is no encode step to get wrong in a way that produces a Secret
   * which mounts as plausible garbage.
   */
  async function createSecret({ namespace, name, data }) {
    await call(`creating Secret ${namespace}/${name}`, {
      method: 'POST',
      path: `/api/v1/namespaces/${encodeURIComponent(namespace)}/secrets`,
      contentType: 'application/json',
      body: {
        apiVersion: 'v1',
        kind: 'Secret',
        type: 'Opaque',
        metadata: { name, namespace },
        stringData: data,
      },
    });
  }

  /**
   * Add one key to an existing Secret. `effects.patchSecretKey`.
   *
   * CREATE-ONLY WITHOUT A RACE, WHICH TAKES BOTH HALVES BELOW
   * There is no server-side "add this map key only if absent" — `add` in JSON
   * Patch replaces an existing member, and a merge patch overwrites. So the
   * absence check has to happen here, and a bare check-then-act would be a race
   * that create-only exists to rule out. The fix is that the check and the write
   * are tied together by `resourceVersion`: a merge patch carrying
   * `metadata.resourceVersion` is a conditional update, and the API server
   * answers 409 if the Secret changed at all since the read. So either nothing
   * touched it between the two calls and the absence we observed still holds, or
   * the write is refused. Neither branch can overwrite a value.
   *
   * ⚠️ The read answers 200 with every existing value in `data`. `keysOf` —
   * `observe.js`'s, not a second copy — takes the key names and the rest is
   * discarded with the response. Nothing about that response is put into an
   * error message; see the header.
   */
  async function patchSecretKey({ namespace, name, key, value }) {
    const at = `${namespace}/${name}#${key}`;
    const { text } = await call(`reading Secret ${namespace}/${name} before adding ${key}`, {
      method: 'GET',
      path: `/api/v1/namespaces/${encodeURIComponent(namespace)}/secrets/${encodeURIComponent(name)}`,
    });
    const secret = parseJson(text);
    const resourceVersion = secret?.metadata?.resourceVersion;
    if (typeof resourceVersion !== 'string' || resourceVersion === '') {
      // Without it the patch below stops being conditional and quietly becomes
      // the unguarded overwrite this function is built to avoid. Refuse instead.
      throw new Error(`refusing to patch ${at}: the API returned the Secret with no metadata.resourceVersion, so the write could not be made conditional`);
    }
    if (keysOf(secret).includes(key)) {
      throw new Error(`refusing to patch ${at}: the key is already present, and this provisioner never overwrites a value it did not create`);
    }

    await call(`adding ${key} to Secret ${namespace}/${name}`, {
      method: 'PATCH',
      path: `/api/v1/namespaces/${encodeURIComponent(namespace)}/secrets/${encodeURIComponent(name)}`,
      contentType: 'application/merge-patch+json',
      // `stringData` only. A merge patch sends exactly what it names, so the
      // other keys' values are neither re-sent nor even referenced — they were
      // read once, above, and discarded.
      body: { metadata: { resourceVersion }, stringData: { [key]: value } },
    });
  }

  return { listSecrets, observeNamespaces, createSecret, patchSecretKey };
}

/**
 * The real transport. Deliberately the dullest function in the repo.
 *
 * `rejectUnauthorized` is never set — leaving it at Node's default of `true` is
 * what makes `ca` mean anything. It is called out because turning it off is the
 * standard way a self-signed-certificate error gets "fixed", and doing so here
 * would mean this pod hands its token to whatever answers on that address.
 */
export function httpsTransport(config, { timeoutMs = DEFAULT_TIMEOUT_MS } = {}) {
  return ({ method, path, headers, body }) =>
    new Promise((resolve, reject) => {
      const req = https.request(
        { host: config.host, port: config.port, path, method, headers, ca: config.ca },
        (res) => {
          let text = '';
          res.setEncoding('utf8');
          res.on('data', (chunk) => { text += chunk; });
          res.on('end', () => resolve({ status: res.statusCode, text }));
        },
      );
      req.setTimeout(timeoutMs, () => {
        // `destroy` with an error, not `abort`: a plain destroy ends the socket
        // and the promise never settles, which on `/reconcile` is a request that
        // hangs until the client gives up and a lock that is still held.
        req.destroy(new Error(`timed out after ${timeoutMs}ms`));
      });
      req.on('error', reject);
      if (body !== undefined) req.write(body);
      req.end();
    });
}
