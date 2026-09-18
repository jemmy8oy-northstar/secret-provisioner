// The HTTP surface: exactly three routes, and the rules that make an
// unauthenticated one safe. Slice 5 of claude-code-bot#97.
//
// James settled the shape on claude-code-bot#97, verbatim: "I think I would
// prefer a reconcile endpoint rather than a job, you can hit this. It can be
// internal only, not exposed to the web, but still relies on the defined
// config."
//
//   GET  /status     what exists — namespace, secret, key, state. Never a value.
//   GET  /plan       what a reconcile WOULD do. Changes nothing.
//   POST /reconcile  does it. Takes no body.
//
// THE HANDLERS ARE INJECTED, AND THAT IS NOT JUST TESTABILITY
// Everything below is about the shape of a request and the shape of a reply.
// None of it knows what a Secret is. So the rules that make an unauthenticated
// endpoint safe can be decided by a test rather than by reasoning about a
// cluster nobody can reach from here — and the routing can land before the
// `/plan` handler exists, which it has to, because where the planner lives is
// still an open question (secret-provisioner#6).
//
// WHY UNAUTHENTICATED IS SAFE, AND WHICH PART OF THAT THIS FILE OWNS
// James's argument is that the service is ClusterIP with no Ingress at all, so
// anyone who can reach it already holds cluster credentials and could read the
// Secrets directly. That is true and it is not the whole argument, because it
// makes reachability the only control — and a compromised pod in the same
// cluster is reachable. The rest of the argument is this file's job:
//
//   - `/reconcile` TAKES NO BODY, and a request carrying one is refused. The
//     declarations in oke-fleet are the only input, so there is no payload
//     through which a caller could smuggle a value, a name, or a namespace.
//     The README states this; without the refusal below it is a description of
//     intent rather than a property. A cross-origin `text/plain` POST needs no
//     preflight, so "a browser cannot call this" is not a control either
//     [[cors-blocks-the-read-not-the-write]] — refusing the body is.
//   - A GET NEVER WRITES. `/reconcile` answers 405 to GET rather than doing the
//     work, so no link, crawler, probe or prefetch can provision anything.
//   - ONE RECONCILE AT A TIME. Two concurrent runs plan against the same
//     observation and then race to create the same objects. Create-only means
//     the loser is refused by the API rather than corrupting anything, so the
//     damage is a report full of 409s and 42710s that looks like a broken
//     cluster. The second caller gets 409 instead.
//
// ERRORS DO NOT ECHO
// A handler's thrown error may carry anything — a Postgres error quotes the
// statement that failed, and that statement contains a password. The response
// body is assembled here from a fixed string and the handler's own structured
// reply, never from an exception's message. The log is a different question and
// a different file; what leaves over HTTP is decided here.

// The whole surface. Written as data so "what routes exist" is one readable
// list rather than a chain of ifs, and so the 404 case cannot drift away from
// it.
const ROUTES = [
  { method: 'GET', path: '/status', handler: 'status' },
  { method: 'GET', path: '/plan', handler: 'plan' },
  { method: 'POST', path: '/reconcile', handler: 'reconcile', exclusive: true, rejectsBody: true },
];

export const SURFACE = Object.freeze(ROUTES.map((r) => `${r.method} ${r.path}`));

/**
 * Which route, if any, a request is for.
 *
 * The path is compared EXACTLY, after the query string is discarded. No
 * trailing-slash aliasing and no prefix matching: `/reconcile/` and
 * `/reconcileX` are not this endpoint, and a surface with one write on it
 * should have exactly one spelling of that write.
 *
 * @param {string} method
 * @param {string} url the raw `req.url`
 * @returns {{route: object|null, path: string, allowed: string[]}}
 *   `allowed` is empty when the path is unknown, which is what separates "no
 *   such route" (404) from "wrong method for a real route" (405). The 405 is
 *   the one that matters: GET /reconcile has to be a visible refusal, not a
 *   404 that hides the fact something tried to provision over a link.
 *
 *   The normalised path comes back too, so the 405 cannot recompute it a
 *   second way and disagree with the match that produced it.
 */
export function match(method, url) {
  // `req.url` is a path, possibly with a query and a fragment. Splitting on
  // both means `/plan?x=1` is `/plan`, and — the part worth stating — that a
  // query string can never reach a handler, so there is no way to pass an
  // argument to an endpoint that is specified as taking none.
  const path = String(url ?? '').split('#')[0].split('?')[0];
  const onPath = ROUTES.filter((r) => r.path === path);
  return {
    route: onPath.find((r) => r.method === method) ?? null,
    path,
    allowed: onPath.map((r) => r.method),
  };
}

/**
 * Has this request got a body? `/reconcile` refuses one.
 *
 * Checked from the headers rather than by reading the stream, because reading
 * it is the thing being refused: a body that is never read cannot be logged,
 * parsed, or mistaken for input by a later change to this file.
 *
 * Both spellings matter. `content-length: 0` is not a body, and a chunked
 * request carries no content-length at all — so a check on either header alone
 * lets one of the two shapes through.
 */
export function carriesBody(headers) {
  const length = Number(headers?.['content-length']);
  if (Number.isFinite(length) && length > 0) return true;
  return String(headers?.['transfer-encoding'] ?? '').toLowerCase().includes('chunked');
}

/** A reply, as data. Serialising it is the caller's job and happens in one place. */
const reply = (status, body) => ({ status, body });

/**
 * Decide the whole response for one request.
 *
 * Pure except for calling the handler: takes a request's method, url and
 * headers, returns `{status, body}`. Every rule above is decided here and
 * `serve` below does nothing but plumbing.
 *
 * @param {{method: string, url: string, headers: object}} request
 * @param {Record<string, () => Promise<object>>} handlers
 * @param {{inFlight: {reconcile: boolean}}} state
 */
export async function respond(request, handlers, state) {
  const { route, allowed } = match(request.method, request.url);

  if (!route) {
    // 405 names the methods that DO work on this path. Without it, `GET
    // /reconcile` and a typo produce the same 404 and nobody learns that
    // something tried to provision with a GET.
    if (allowed.length > 0) {
      return { ...reply(405, { error: 'method not allowed', allowed }), headers: { allow: allowed.join(', ') } };
    }
    return reply(404, { error: 'no such route', routes: SURFACE });
  }

  if (route.rejectsBody && carriesBody(request.headers)) {
    return reply(400, {
      error: 'this endpoint takes no body',
      why: 'the declarations in oke-fleet are the only input; a request body could only be an attempt to pass one another way',
    });
  }

  if (route.exclusive) {
    // Check and set with no `await` between them. Node runs one request's
    // synchronous stretch to completion, so this pair is atomic — and it is
    // only atomic because nothing is awaited here. Any future `await` slipped
    // between these two lines reopens the race while every test still passes.
    if (state.inFlight[route.handler]) {
      // Not a queue. Two reconciles racing to create the same objects produce
      // a report full of "already exists" that reads like a broken cluster,
      // and the run is on demand with a human watching — so the honest answer
      // is "one is already going", not a silent wait.
      return reply(409, { error: 'a reconcile is already running', retry: 'when it finishes' });
    }
    state.inFlight[route.handler] = true;
  }

  try {
    return reply(200, await handlers[route.handler]());
  } catch (error) {
    // The handler's message is deliberately NOT in the response. A Postgres
    // error quotes the statement that failed and that statement contains a
    // password; `execute.js` redacts what it puts in a report, but an
    // exception escaping before that point has been through nothing.
    // Re-thrown so the caller can log it where redaction is that layer's job.
    return { ...reply(500, { error: `${route.method} ${route.path} failed`, detail: 'see the pod log' }), thrown: error };
  } finally {
    if (route.exclusive) state.inFlight[route.handler] = false;
  }
}

/**
 * The `http.createServer` listener.
 *
 * @param {Record<string, () => Promise<object>>} handlers
 * @param {(error: unknown) => void} [onError] where a thrown handler error goes
 */
export function serve(handlers, onError = () => {}) {
  // One object, created once, so "is a reconcile running" is shared across
  // requests. A per-request state would make `exclusive` silently do nothing —
  // the guard would exist, pass its unit test, and never once be true.
  const state = { inFlight: { reconcile: false } };

  return async (req, res) => {
    const result = await respond({ method: req.method, url: req.url, headers: req.headers }, handlers, state);
    if (result.thrown) onError(result.thrown);
    const body = JSON.stringify(result.body, null, 2);
    res.writeHead(result.status, { 'content-type': 'application/json', ...(result.headers ?? {}) });
    // HEAD is not in the surface, so every response here has a body.
    res.end(body);
  };
}
