// Run with: npm test  (i.e. bare `node --test`, which auto-discovers)
//
// Everything here drives `kubeClient` through a recording transport, so what is
// asserted is the exact request the cluster would receive — method, path,
// content type, and the body byte for byte. That is the whole point of the
// seam: the decisions in `kube.js` are about the shape of four HTTP calls, and
// a shape is only checked if something compares it to a literal.
//
// ⚠️ The literals below are deliberately spelled out rather than built from the
// module's own constants. A test that derives the expected path from the code
// under test passes for a path of `/undefined` [[pin-a-seam-from-both-sides]].
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  inClusterConfig,
  kubeClient,
  redactToken,
  SERVICE_ACCOUNT_DIR,
} from './kube.js';

const TOKEN = 'eyJhbGciOiJSUzI1NiJ9.a-real-looking-service-account-token.signature';
const CONFIG = { host: '10.96.0.1', port: 443, token: TOKEN, ca: '-----BEGIN CERTIFICATE-----\nx\n-----END CERTIFICATE-----\n' };

/**
 * A transport that records every request and replays a scripted reply.
 *
 * `replies` is consumed in order. Running out is an assertion failure rather
 * than an undefined read, because "the client made a call the test did not
 * expect" is a finding, and `undefined.status` would surface it as a TypeError
 * three frames away from the cause.
 */
function recorder(replies) {
  const sent = [];
  const queue = [...replies];
  const transport = async (request) => {
    sent.push(request);
    assert.ok(queue.length > 0, `unexpected extra request: ${request.method} ${request.path}`);
    const next = queue.shift();
    if (next instanceof Error) throw next;
    return next;
  };
  return { sent, transport, get drained() { return queue.length === 0; } };
}

const okJson = (status, body) => ({ status, text: JSON.stringify(body) });
const apiStatus = (status, message, reason) =>
  okJson(status, { kind: 'Status', apiVersion: 'v1', status: 'Failure', message, reason, code: status });

// A Secret exactly as the API returns one on a 200: values present, base64, in
// `data`, beside the names.
const liveSecret = (resourceVersion = '80421') => ({
  kind: 'Secret',
  apiVersion: 'v1',
  metadata: { name: 'balenthiran-secrets', namespace: 'balenthiran', resourceVersion },
  type: 'Opaque',
  data: {
    Jwt__Secret: 'ZGVhZGJlZWZkZWFkYmVlZg==',
    Admin__Key: 'YWRtaW4ta2V5',
  },
});

// ─── configuration ──────────────────────────────────────────────────────────

test('in-cluster config reads the kubelet mount and the env', () => {
  const files = {
    [`${SERVICE_ACCOUNT_DIR}/token`]: `${TOKEN}\n`,
    [`${SERVICE_ACCOUNT_DIR}/ca.crt`]: CONFIG.ca,
  };
  const config = inClusterConfig({
    env: { KUBERNETES_SERVICE_HOST: '10.96.0.1', KUBERNETES_SERVICE_PORT_HTTPS: '443' },
    read: (p) => files[p],
  });
  // The trailing newline the kubelet writes must be gone: it would ride into
  // the Authorization header, and an HTTP header value containing a newline is
  // rejected by Node with a message about invalid characters rather than
  // anything resembling "your token has a newline in it".
  assert.equal(config.token, TOKEN);
  assert.equal(config.host, '10.96.0.1');
  assert.equal(config.port, 443);
});

test('no host means no default — it refuses rather than guessing a cluster', () => {
  assert.throws(
    () => inClusterConfig({ env: {}, read: () => 'x' }),
    /KUBERNETES_SERVICE_HOST is not set/,
  );
});

test('an empty CA is refused, because the fallback is the public root store', () => {
  const files = { [`${SERVICE_ACCOUNT_DIR}/token`]: TOKEN, [`${SERVICE_ACCOUNT_DIR}/ca.crt`]: '   \n' };
  assert.throws(
    () => inClusterConfig({ env: { KUBERNETES_SERVICE_HOST: 'h' }, read: (p) => files[p] }),
    /refusing to fall back to the public root store/,
  );
});

test('an empty token is refused', () => {
  const files = { [`${SERVICE_ACCOUNT_DIR}/token`]: '\n', [`${SERVICE_ACCOUNT_DIR}/ca.crt`]: CONFIG.ca };
  assert.throws(
    () => inClusterConfig({ env: { KUBERNETES_SERVICE_HOST: 'h' }, read: (p) => files[p] }),
    /token .* is empty/,
  );
});

test('a non-numeric port is refused rather than composed into a URL', () => {
  const files = { [`${SERVICE_ACCOUNT_DIR}/token`]: TOKEN, [`${SERVICE_ACCOUNT_DIR}/ca.crt`]: CONFIG.ca };
  assert.throws(
    () => inClusterConfig({
      env: { KUBERNETES_SERVICE_HOST: 'h', KUBERNETES_SERVICE_PORT_HTTPS: 'tcp://10.96.0.1:443' },
      read: (p) => files[p],
    }),
    /KUBERNETES_SERVICE_PORT must be an integer/,
  );
});

// ─── listing, and the one property the observer depends on ──────────────────

test('listSecrets asks the right URL with the right credential', async () => {
  const rec = recorder([okJson(200, { kind: 'SecretList', items: [liveSecret()] })]);
  const listing = await kubeClient({ config: CONFIG, transport: rec.transport }).listSecrets('balenthiran');

  assert.deepEqual(rec.sent[0].method, 'GET');
  assert.equal(rec.sent[0].path, '/api/v1/namespaces/balenthiran/secrets');
  assert.equal(rec.sent[0].headers.authorization, `Bearer ${TOKEN}`);
  assert.equal(rec.sent[0].body, undefined);
  assert.equal(listing.items.length, 1);
});

test('a namespace name is encoded into the path, never concatenated', async () => {
  const rec = recorder([okJson(200, { kind: 'SecretList', items: [] })]);
  await kubeClient({ config: CONFIG, transport: rec.transport }).listSecrets('a b/../../nodes');
  assert.equal(rec.sent[0].path, '/api/v1/namespaces/a%20b%2F..%2F..%2Fnodes/secrets');
});

test('a 200 that is not a SecretList is a failure, not an empty namespace', async () => {
  // The realistic source is a proxy or a mesh sidecar answering 200 with HTML.
  const rec = recorder([{ status: 200, text: '<html>Gateway</html>' }]);
  await assert.rejects(
    () => kubeClient({ config: CONFIG, transport: rec.transport }).listSecrets('balenthiran'),
    /is not a v1.SecretList/,
  );
});

test('a failed namespace carries an error and NO listing property', async () => {
  // This is the property the entire codebase is arranged around. `observation()`
  // branches on `result.error !== undefined`, and a `listing: {items: []}` here
  // would make a transient 503 read as "this namespace holds no Secrets" —
  // after which the planner plans a create for a Secret that already exists.
  const rec = recorder([
    okJson(200, { kind: 'SecretList', items: [] }),
    apiStatus(503, 'the server is currently unable to handle the request', 'ServiceUnavailable'),
    okJson(200, { kind: 'SecretList', items: [liveSecret()] }),
  ]);
  const results = await kubeClient({ config: CONFIG, transport: rec.transport })
    .observeNamespaces(['empty-ns', 'unreadable-ns', 'balenthiran']);

  assert.equal(results.length, 3, 'a failure is reported, not dropped');
  assert.ok(!('error' in results[0]), 'a genuinely empty namespace has no error');
  assert.deepEqual(results[0].listing.items, []);

  assert.ok(results[1].error instanceof Error);
  assert.equal(results[1].error.status, 503);
  assert.ok(
    !Object.prototype.hasOwnProperty.call(results[1], 'listing'),
    'a failed namespace must not carry a listing of any kind, not even an empty one',
  );

  assert.equal(results[2].listing.items.length, 1);
  assert.ok(rec.drained);
});

test('one namespace failing does not stop the others being read', async () => {
  const rec = recorder([
    apiStatus(403, 'secrets is forbidden', 'Forbidden'),
    okJson(200, { kind: 'SecretList', items: [] }),
  ]);
  const results = await kubeClient({ config: CONFIG, transport: rec.transport })
    .observeNamespaces(['forbidden-ns', 'fine-ns']);
  assert.ok(results[0].error);
  assert.ok(!results[1].error);
});

test('a transport-level failure is a namespace failure too, not a crash', async () => {
  const rec = recorder([new Error('getaddrinfo ENOTFOUND kubernetes.default.svc')]);
  const [result] = await kubeClient({ config: CONFIG, transport: rec.transport }).observeNamespaces(['balenthiran']);
  assert.match(result.error.message, /ENOTFOUND/);
  assert.ok(!Object.prototype.hasOwnProperty.call(result, 'listing'));
});

// ─── create-only ────────────────────────────────────────────────────────────

test('createSecret POSTs to the collection with stringData', async () => {
  const rec = recorder([okJson(201, { kind: 'Secret' })]);
  await kubeClient({ config: CONFIG, transport: rec.transport })
    .createSecret({ namespace: 'kit', name: 'kit-auth', data: { password: 'deadbeef'.repeat(8) } });

  const [sent] = rec.sent;
  // POST to the COLLECTION. A PUT to the item path is an unconditional replace,
  // which would discard every key not in `data` — and the 409 that makes
  // create-only a guarantee would never be returned.
  assert.equal(sent.method, 'POST');
  assert.equal(sent.path, '/api/v1/namespaces/kit/secrets');
  assert.equal(sent.headers['content-type'], 'application/json');

  const body = JSON.parse(sent.body);
  assert.equal(body.kind, 'Secret');
  assert.equal(body.type, 'Opaque');
  assert.equal(body.metadata.name, 'kit-auth');
  // `stringData`, so nothing in this repo ever base64-encodes a value.
  assert.deepEqual(body.stringData, { password: 'deadbeef'.repeat(8) });
  assert.equal(body.data, undefined);
});

test('content-length is the byte length, not the character count', async () => {
  const rec = recorder([okJson(201, {})]);
  await kubeClient({ config: CONFIG, transport: rec.transport })
    // A name a human would actually type into a declaration one day.
    .createSecret({ namespace: 'kit', name: 'café-secrets', data: { k: 'v' } });
  const [sent] = rec.sent;
  assert.equal(Number(sent.headers['content-length']), Buffer.byteLength(sent.body));
  assert.notEqual(Number(sent.headers['content-length']), sent.body.length, 'the fixture must actually contain a multi-byte character, or this test asserts nothing');
});

test('a 409 from create surfaces as a Conflict the caller can branch on', async () => {
  const rec = recorder([apiStatus(409, 'secrets "kit-auth" already exists', 'AlreadyExists')]);
  await assert.rejects(
    () => kubeClient({ config: CONFIG, transport: rec.transport })
      .createSecret({ namespace: 'kit', name: 'kit-auth', data: { password: 'x' } }),
    (error) => {
      assert.equal(error.status, 409);
      assert.equal(error.reason, 'AlreadyExists');
      assert.match(error.message, /already exists/);
      return true;
    },
  );
});

// ─── the patch path, and its compare-and-swap ───────────────────────────────

test('patchSecretKey reads, then sends a conditional merge patch of one key', async () => {
  const rec = recorder([okJson(200, liveSecret('80421')), okJson(200, { kind: 'Secret' })]);
  await kubeClient({ config: CONFIG, transport: rec.transport })
    .patchSecretKey({ namespace: 'balenthiran', name: 'balenthiran-secrets', key: 'DATABASE_URL', value: 'Host=pg;Port=5432' });

  const [read, patch] = rec.sent;
  assert.equal(read.method, 'GET');
  assert.equal(read.path, '/api/v1/namespaces/balenthiran/secrets/balenthiran-secrets');

  assert.equal(patch.method, 'PATCH');
  assert.equal(patch.path, '/api/v1/namespaces/balenthiran/secrets/balenthiran-secrets');
  assert.equal(patch.headers['content-type'], 'application/merge-patch+json');

  const body = JSON.parse(patch.body);
  // The precondition. Without it the check above becomes a plain check-then-act
  // race and this function stops being create-only.
  assert.equal(body.metadata.resourceVersion, '80421');
  assert.deepEqual(body.stringData, { DATABASE_URL: 'Host=pg;Port=5432' });
  // The existing keys are neither re-sent nor mentioned.
  assert.equal(body.data, undefined);
  assert.ok(!patch.body.includes('Jwt__Secret'));
});

test('an existing key is refused — this provisioner never overwrites', async () => {
  const rec = recorder([okJson(200, liveSecret())]);
  await assert.rejects(
    () => kubeClient({ config: CONFIG, transport: rec.transport })
      .patchSecretKey({ namespace: 'balenthiran', name: 'balenthiran-secrets', key: 'Jwt__Secret', value: 'new' }),
    /already present/,
  );
  // And, the half that matters: it never got as far as writing.
  assert.equal(rec.sent.length, 1);
});

test('a Secret returned without a resourceVersion is refused, not patched unconditionally', async () => {
  const secret = liveSecret();
  delete secret.metadata.resourceVersion;
  const rec = recorder([okJson(200, secret)]);
  await assert.rejects(
    () => kubeClient({ config: CONFIG, transport: rec.transport })
      .patchSecretKey({ namespace: 'balenthiran', name: 'balenthiran-secrets', key: 'DATABASE_URL', value: 'v' }),
    /could not be made conditional/,
  );
  assert.equal(rec.sent.length, 1);
});

test('a 409 on the patch is the compare-and-swap doing its job', async () => {
  const rec = recorder([
    okJson(200, liveSecret('80421')),
    apiStatus(409, 'Operation cannot be fulfilled on secrets "balenthiran-secrets": the object has been modified', 'Conflict'),
  ]);
  await assert.rejects(
    () => kubeClient({ config: CONFIG, transport: rec.transport })
      .patchSecretKey({ namespace: 'balenthiran', name: 'balenthiran-secrets', key: 'DATABASE_URL', value: 'v' }),
    (error) => {
      assert.equal(error.status, 409);
      assert.equal(error.reason, 'Conflict');
      return true;
    },
  );
});

// ─── what may be quoted, and what may never be ──────────────────────────────

test('no value from a 200 response reaches an error message', async () => {
  // The patch path is the only place this module reads a 200 containing values.
  // Every way out of it — refusal, API failure, transport failure — is checked
  // against every value in the fixture, because it takes exactly one
  // `${JSON.stringify(secret)}` in a future error message to undo it.
  const secret = liveSecret();
  const values = Object.values(secret.data);
  const client = (replies) => kubeClient({ config: CONFIG, transport: recorder(replies).transport });

  const attempts = [
    () => client([okJson(200, secret)])
      .patchSecretKey({ namespace: 'balenthiran', name: 'balenthiran-secrets', key: 'Jwt__Secret', value: 'v' }),
    () => client([okJson(200, secret), apiStatus(409, 'the object has been modified', 'Conflict')])
      .patchSecretKey({ namespace: 'balenthiran', name: 'balenthiran-secrets', key: 'DATABASE_URL', value: 'v' }),
    () => client([okJson(200, secret), new Error('socket hang up')])
      .patchSecretKey({ namespace: 'balenthiran', name: 'balenthiran-secrets', key: 'DATABASE_URL', value: 'v' }),
  ];

  let checked = 0;
  for (const attempt of attempts) {
    await assert.rejects(attempt, (error) => {
      for (const value of values) {
        assert.ok(!error.message.includes(value), `an error message carried ${value}`);
      }
      checked += 1;
      return true;
    });
  }
  assert.equal(checked, attempts.length, 'every attempt must have thrown, or this test passes by not running');
  assert.ok(values.length > 0 && values.every((v) => v.length > 0), 'the fixture must carry real values');
});

test('the service account token never reaches an error message', async () => {
  // Node attaches request options to some socket-level errors, and the token is
  // in a header. So the redaction is applied to the transport failure too, not
  // only to bodies we assemble ourselves.
  const leaky = new Error(`write EPROTO; request was {"headers":{"authorization":"Bearer ${TOKEN}"}}`);
  const rec = recorder([leaky]);
  await assert.rejects(
    () => kubeClient({ config: CONFIG, transport: rec.transport }).listSecrets('balenthiran'),
    (error) => {
      assert.ok(!error.message.includes(TOKEN), 'the token reached an error message');
      assert.match(error.message, /<redacted>/);
      return true;
    },
  );
});

test('a non-2xx body IS quoted, because that is the only diagnosis anyone gets', async () => {
  // The opposite rule to the two tests above, and it has to be asserted too:
  // "redact everything" would be trivially safe and would make every failure
  // read as `the API answered 403`.
  const rec = recorder([apiStatus(403, 'secrets is forbidden: User "system:serviceaccount:secrets:provisioner" cannot list resource "secrets"', 'Forbidden')]);
  await assert.rejects(
    () => kubeClient({ config: CONFIG, transport: rec.transport }).listSecrets('balenthiran'),
    /cannot list resource "secrets"/,
  );
});

test('redactToken leaves a string alone when there is nothing to redact', () => {
  assert.equal(redactToken('plain', TOKEN), 'plain');
  assert.equal(redactToken('plain', ''), 'plain');
  assert.equal(redactToken(`a ${TOKEN} b`, TOKEN), 'a <redacted> b');
});

// ─── the interface the executor already expects ─────────────────────────────

test('the client satisfies the two effects the executor calls on Kubernetes', () => {
  // `execute.js` destructures `effects.createSecret` and `effects.patchSecretKey`
  // by name. They are in different files in different PRs, so a rename that
  // looks harmless in one is a `TypeError` in a live reconcile in the other.
  const client = kubeClient({ config: CONFIG, transport: async () => okJson(200, {}) });
  assert.equal(typeof client.createSecret, 'function');
  assert.equal(typeof client.patchSecretKey, 'function');
  assert.equal(typeof client.listSecrets, 'function');
  assert.equal(typeof client.observeNamespaces, 'function');
});
