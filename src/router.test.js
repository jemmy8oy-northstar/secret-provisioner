// Run with: npm test  (i.e. bare `node --test`, which auto-discovers)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';

import { match, carriesBody, respond, serve, SURFACE } from './router.js';

const handlers = (over = {}) => ({
  status: async () => ({ ok: 'status' }),
  plan: async () => ({ ok: 'plan' }),
  reconcile: async () => ({ ok: 'reconcile' }),
  ...over,
});
const fresh = () => ({ inFlight: { reconcile: false } });
const req = (method, url, headers = {}) => ({ method, url, headers });

// ── the surface is exactly three routes ─────────────────────────────────────

test('the surface is the three routes James specified, and nothing else', () => {
  assert.deepEqual(SURFACE, ['GET /status', 'GET /plan', 'POST /reconcile']);
});

test('an unknown path is 404 and says what does exist', async () => {
  const r = await respond(req('GET', '/secrets'), handlers(), fresh());
  assert.equal(r.status, 404);
  assert.deepEqual(r.body.routes, SURFACE);
});

test('the path is matched EXACTLY — no trailing slash, no prefix', async () => {
  // A surface with exactly one write on it should have exactly one spelling of
  // that write. `/reconcile/` and `/reconcileX` are not this endpoint.
  for (const path of ['/reconcile/', '/reconcileX', '/x/reconcile', '/Reconcile']) {
    const r = await respond(req('POST', path), handlers(), fresh());
    assert.equal(r.status, 404, `${path} reached the endpoint`);
  }
});

test('a query string is discarded, so no argument can reach an endpoint specified as taking none', async () => {
  const r = await respond(req('GET', '/plan?namespace=balenthiran&force=1'), handlers(), fresh());
  assert.equal(r.status, 200);
  assert.equal(match('GET', '/plan?x=1#y').path, '/plan');
});

// ── a GET never writes ──────────────────────────────────────────────────────

test('GET /reconcile is 405 and does NOT run the handler', async () => {
  // The failure this prevents: a link, a crawler, a browser prefetch or an
  // uptime probe provisioning the estate. A 404 here would work just as well
  // for the caller and would hide the fact that it happened.
  let ran = false;
  const r = await respond(req('GET', '/reconcile'), handlers({ reconcile: async () => { ran = true; return {}; } }), fresh());
  assert.equal(r.status, 405);
  assert.equal(ran, false);
  assert.deepEqual(r.body.allowed, ['POST']);
  assert.equal(r.headers.allow, 'POST');
});

test('POST to a read route is 405, not a silent 200', async () => {
  const r = await respond(req('POST', '/plan'), handlers(), fresh());
  assert.equal(r.status, 405);
  assert.deepEqual(r.body.allowed, ['GET']);
});

test('405 and 404 are different answers — a real path with the wrong method must not read as a typo', async () => {
  assert.equal((await respond(req('GET', '/reconcile'), handlers(), fresh())).status, 405);
  assert.equal((await respond(req('GET', '/reconcil'), handlers(), fresh())).status, 404);
});

// ── /reconcile takes no body ────────────────────────────────────────────────

test('a body on /reconcile is refused, and the handler does not run', async () => {
  // This is the property that makes an unauthenticated endpoint safe, and
  // until it is enforced it is only a sentence in the README. No body means
  // no payload through which a caller could smuggle a value, a name or a
  // namespace — reachability stops being the only control.
  let ran = false;
  const h = handlers({ reconcile: async () => { ran = true; return {}; } });
  const r = await respond(req('POST', '/reconcile', { 'content-length': '2' }), h, fresh());
  assert.equal(r.status, 400);
  assert.equal(ran, false);
});

test('a CHUNKED body is refused too — it carries no content-length at all', () => {
  // A check on content-length alone lets this shape straight through, and it
  // is the shape a cross-origin `text/plain` POST can take. "A browser cannot
  // call this" is not a control; refusing the body is.
  assert.equal(carriesBody({ 'transfer-encoding': 'chunked' }), true);
  assert.equal(carriesBody({ 'transfer-encoding': 'Chunked' }), true);
  assert.equal(carriesBody({ 'content-length': '2' }), true);
});

test('content-length: 0 is NOT a body — a plain curl -X POST must work', () => {
  // `curl -X POST` sends `content-length: 0`. Treating that as a body would
  // make the endpoint unusable by the one command anyone would reach for.
  assert.equal(carriesBody({ 'content-length': '0' }), false);
  assert.equal(carriesBody({}), false);
  assert.equal(carriesBody(undefined), false);
});

test('a body is refused WITHOUT being read', async () => {
  // Checked from the headers, never from the stream: a body that is never read
  // cannot be logged, parsed, or mistaken for input by a later edit. The
  // request object here has no readable stream at all, which is the assertion.
  const r = await respond(req('POST', '/reconcile', { 'content-length': '9' }), handlers(), fresh());
  assert.equal(r.status, 400);
  assert.match(r.body.why, /only input/);
});

// ── one reconcile at a time ─────────────────────────────────────────────────

test('a second reconcile while one is running is 409, not a queue and not a race', async () => {
  // Two runs plan against the same observation and then race to create the
  // same objects. Create-only means the loser is refused by the API rather
  // than corrupting anything — so the damage is a report full of "already
  // exists" that reads like a broken cluster.
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const state = fresh();
  const h = handlers({ reconcile: async () => { await gate; return { ok: 'reconcile' }; } });

  const first = respond(req('POST', '/reconcile'), h, state);
  const second = await respond(req('POST', '/reconcile'), h, state);
  assert.equal(second.status, 409);

  release();
  assert.equal((await first).status, 200);
});

test('the lock is released after a run, including a failed one', async () => {
  const state = fresh();
  const boom = handlers({ reconcile: async () => { throw new Error('nope'); } });
  assert.equal((await respond(req('POST', '/reconcile'), boom, state)).status, 500);
  // If the lock leaked, this is 409 forever and the only fix is a pod restart.
  assert.equal((await respond(req('POST', '/reconcile'), handlers(), state)).status, 200);
});

test('reads are never locked — /status and /plan work while a reconcile runs', async () => {
  const state = { inFlight: { reconcile: true } };
  assert.equal((await respond(req('GET', '/status'), handlers(), state)).status, 200);
  assert.equal((await respond(req('GET', '/plan'), handlers(), state)).status, 200);
});

// ── errors do not echo ──────────────────────────────────────────────────────

test('a handler\'s error message never reaches the response body', async () => {
  // A Postgres error quotes the statement that failed, and that statement
  // contains a password. execute.js redacts what it puts in a REPORT; an
  // exception escaping before that point has been through nothing.
  const leak = 'CREATE ROLE app PASSWORD \'deadbeefdeadbeef\'';
  const r = await respond(req('POST', '/reconcile'), handlers({ reconcile: async () => { throw new Error(`syntax error at ${leak}`); } }), fresh());
  assert.equal(r.status, 500);
  assert.equal(JSON.stringify(r.body).includes('deadbeef'), false, 'a generated value reached the HTTP response');
  assert.equal(JSON.stringify(r.body).includes('PASSWORD'), false);
});

test('the error is still handed to the caller to log — refusing to echo is not swallowing', async () => {
  const thrown = new Error('boom');
  const r = await respond(req('GET', '/status'), handlers({ status: async () => { throw thrown; } }), fresh());
  assert.equal(r.thrown, thrown);
});

// ── over a real socket ──────────────────────────────────────────────────────

test('end to end: a real server, a real client, and every rule still holds', async () => {
  // The unit tests above drive `respond` directly. This one drives an actual
  // http.Server, because the plumbing in `serve` — the shared state object,
  // the status line, the allow header — is where a rule proven at the layer
  // below can still fail to be wired up [[both-layers-right-assembly-broken]].
  const logged = [];
  const server = createServer(serve(handlers({ plan: async () => { throw new Error('secret-value-here'); } }), (e) => logged.push(e)));
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const base = `http://127.0.0.1:${server.address().port}`;

  try {
    const status = await fetch(`${base}/status`);
    assert.equal(status.status, 200);
    assert.equal(status.headers.get('content-type'), 'application/json');
    assert.deepEqual(await status.json(), { ok: 'status' });

    const get = await fetch(`${base}/reconcile`);
    assert.equal(get.status, 405);
    assert.equal(get.headers.get('allow'), 'POST');

    const empty = await fetch(`${base}/reconcile`, { method: 'POST' });
    assert.equal(empty.status, 200, 'a plain POST with no body must be the working case');

    const withBody = await fetch(`${base}/reconcile`, { method: 'POST', body: 'x' });
    assert.equal(withBody.status, 400);

    const failed = await fetch(`${base}/plan`);
    assert.equal(failed.status, 500);
    assert.equal((await failed.text()).includes('secret-value-here'), false);
    assert.equal(logged.length, 1, 'the error must reach the log even though it never reaches the wire');
  } finally {
    server.close();
    await once(server, 'close');
  }
});

test('the in-flight lock is shared across requests, not created per request', async () => {
  // A per-request state object would make the exclusivity guard silently inert
  // — it would exist, pass its own unit test, and never once be true.
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  const server = createServer(serve(handlers({ reconcile: async () => { await gate; return {}; } })));
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const base = `http://127.0.0.1:${server.address().port}`;

  try {
    const first = fetch(`${base}/reconcile`, { method: 'POST' });
    // Give the first request time to reach the handler before racing it.
    await new Promise((r) => setTimeout(r, 50));
    const second = await fetch(`${base}/reconcile`, { method: 'POST' });
    assert.equal(second.status, 409);
    release();
    assert.equal((await first).status, 200);
  } finally {
    server.close();
    await once(server, 'close');
  }
});
