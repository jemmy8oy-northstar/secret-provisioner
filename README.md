# secret-provisioner

Creates the Kubernetes Secrets and Postgres databases that the apps in
[`oke-fleet`](https://github.com/jemmy8oy-northstar/oke-fleet) declare, so a new
app can be provisioned **before** it is released rather than at the same moment.

**No secret value is ever stored in this repo, and none is ever logged.** The
values exist only in the cluster — James's ruling on
[claude-code-bot#97](https://github.com/jemmy8oy-northstar/claude-code-bot/issues/97):
*"The secrets should live only in the cluster. Definitely not in git."*

## The shape, and who decided it

Every line here is a decision James made on
[claude-code-bot#97](https://github.com/jemmy8oy-northstar/claude-code-bot/issues/97).
It is written down because a decision that lives only in a thread stops being
readable about six comments later.

| decision | what it means here |
| --- | --- |
| **Its own repo**, not part of `oke-fleet` | `oke-fleet` is a GitOps config repo with no build. Keeping the code out of it means adding a declaration is a data change, not a container rebuild. |
| **Declarations stay in `oke-fleet/secrets/*.json`** | read over plain HTTPS at reconcile time. No credential is needed, because `oke-fleet` is public. |
| **Read the `dev` branch, not `main`** | Argo CD reads the default branch (`targetRevision: HEAD` → `main`). Reading `dev` is what makes provisioning a *precondition* of release instead of simultaneous with it. |
| **A Deployment, not a Job or CronJob** | it serves a status endpoint, so it has to outlive a single run. |
| **On demand — no timer** | nothing runs unless someone hits `/reconcile`. A create-only provisioner that only ever runs while a human is watching has a much smaller blast radius than one that acts at 3am. |
| **Internal only — no Ingress at all** | not an Ingress with authentication in front of it. Reaching it requires `kubectl port-forward`, which requires cluster credentials. |
| **Create-only. Never delete, never overwrite.** | a key that is already present is left alone, even when it looks wrong. |
| **A non-superuser provisioning role** | Postgres 16 restricts `CREATEROLE` so a role may only alter roles it created itself, so this pod cannot touch `postgres` or any role it did not make. |

## Routes

| route | what it does |
| --- | --- |
| `GET /status` | what exists — namespace, secret, key, state. Never a value. |
| `GET /plan` | what a reconcile *would* do. Changes nothing. |
| `POST /reconcile` | does it. **Takes no body** — the declarations are the only input. |

`/reconcile` is unauthenticated, and that is deliberate rather than an omission:
it accepts no input, it is idempotent, and it is only reachable from inside the
cluster — where anyone who could call it could already read the Secrets
directly. There is no payload through which a value could be injected.

## Why this repo is public

Nothing in it is a secret, and the declarations it acts on are already public in
`oke-fleet`. Making the code private would split one disclosure across two
visibilities and protect nothing.

The password generator must therefore be safe to publish **by construction** —
`crypto.randomBytes`, no seed, no cleverness. A generator that is weak when
published was weak when it was private; publication only removes the option of
getting away with it.

## What is built

- ✅ **The planner** — pure: declarations + an observation → the list of actions.
  Lives in
  [`oke-fleet`](https://github.com/jemmy8oy-northstar/oke-fleet/blob/dev/scripts/plan-secrets.mjs)
  next to the schema it reads, and is consumed here as source rather than as a
  package.
- ✅ **The observer** (`src/observe.js`) — turns Kubernetes and Postgres
  responses into that observation. Pure; the clients live at the edge.
- ⬜ The Kubernetes and Postgres clients, the executor, the HTTP surface.
- ⬜ `Dockerfile`, chart, build workflow.

### The one property the observer exists to hold

The planner refuses to plan against a namespace the observation does not list,
because *"this namespace holds no Secrets"* and *"I never listed this
namespace"* arrive as the same empty object — and a failed lookup read as the
first plans a **create** for a Secret that already exists, which deletes every
key not in the plan. `around-the-world-secrets` holds five keys, two of them
`Jwt__Secret` and `Admin__Key`, so the visible symptom would be every guest
signed out mid-party rather than an error anyone would see in a log.

That refusal is worth nothing unless the observer never lies about what it
enumerated. So a namespace whose listing **failed** does not appear in
`namespaces`, even though the natural way to write the loop puts it there. The
seam is pinned from both sides, and a test asserts each half.

## Running the tests

```sh
npm test
```

⚠️ Not `node --test src/` — naming a directory makes Node resolve it as a module
and fail with `MODULE_NOT_FOUND`, which looks exactly like a broken suite.

## Branches

`main` is the released state; work happens on `dev` via pull requests.
