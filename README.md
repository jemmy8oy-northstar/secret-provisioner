# secret-provisioner

Creates the Kubernetes Secrets and Postgres databases that the apps in
[`oke-fleet`](https://github.com/jemmy8oy-northstar/oke-fleet) declare, so that a
new app can be provisioned **before** it is released rather than at the same
moment.

**No secret value is ever stored in this repo.** The values exist only in the
cluster.

Work happens on `dev`. This branch is the initial commit.
