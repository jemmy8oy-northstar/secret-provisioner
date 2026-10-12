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

## It is written in C#

James chose C# on
[kit#176](https://github.com/jemmy8oy-northstar/kit/issues/176) (2026-10-09),
which matches his house stack (.NET backend, React frontend, optional Postgres).
The code lives in `backend/` in web-template's layout: interfaces in
`Abstractions`, models in `DataModels`, pure logic in `Services`, and the
Kubernetes, Postgres and GitHub clients in `Database`. It was ported from
JavaScript, which was deleted once the C# service ran (#9).

## What is built

- ✅ **The planner** (`Services/Planner.cs`): pure, taking declarations and an
  observation and returning the list of actions. It moved here from
  [oke-fleet#9](https://github.com/jemmy8oy-northstar/oke-fleet/pull/9)
  under James's ruling on
  [#6](https://github.com/jemmy8oy-northstar/secret-provisioner/issues/6): the
  planner lives with its only consumer, and the declarations stay in
  `oke-fleet`. It is scored against the JavaScript planner's own output; see
  below.
- ✅ **The observer** (`Services/Observer.cs`): turns Kubernetes and Postgres
  responses into that observation. Pure; the clients live at the edge.
- ✅ **The contracts** for the Kubernetes, Postgres and declarations clients
  (`Abstractions/Services`).
- ✅ The executor, the three clients and the HTTP surface.
- ✅ `Dockerfile`, chart, build workflow.

### How the port is checked

`tools/plan-goldens.mjs` (now deleted; it is in git history) ran the JavaScript
planner from `oke-fleet` over 19 scenarios. It wrote each scenario's input
together with the planner's real output to `backend/Balenthiran.SecretProvisioner.Tests/Fixtures/Plan/`. The C#
planner must produce the same plan for every one. The expected side never
passes through C#, so a serialiser bug can't hide by appearing on both sides.

`mutants/planner.json` lists 15 specific ways to break the planner and the
observer, each naming the test that must fail. All 15 are killed:

```sh
node ../claude-code-bot/tools/mutate-spec.js mutants/planner.json --keep-going
```

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
dotnet test backend/Balenthiran.SecretProvisioner.slnx
```

## Branches

`main` is the released state; work happens on `dev` via pull requests.
