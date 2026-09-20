# M3 controlled activation

Status: normative operations procedure.
Date: 2026-09-20.

M3 runtime code ships with official War API ingestion disabled by default.

## Preconditions

Do not enable source collection until:

- the current EF migration bundle has been applied;
- mandatory restore/format/build/tests are green;
- the Worker starts successfully with WarApi:Enabled=false;
- the non-blocking Live-1 canary has succeeded recently;
- no M2 lease/fence/evidence invariant is failing.

## First activation

Enable only Live-1:

~~~text
WarApi__Enabled=true
WarApi__EnabledShards__0=live-1
WarApi__EnableDev=false
~~~

Do not enable Live-2, Live-3 or Dev during the first observation window.

The Worker will idempotently register:

- source official-war-api;
- shard live-1;
- root war endpoint;
- root maps endpoint;
- deterministic bootstrap jobs.

The maps representation will later discover regional endpoints.

## Observe before expanding

During the first controlled run verify:

- request outcomes and latency;
- 200/304 ratio;
- source cache delay;
- Retry-After handling;
- body/header limit hits;
- uncertain exchanges;
- parse outcomes;
- unknown code/property counts;
- structural fingerprint changes;
- planner repair count;
- successor scheduling behavior;
- PostgreSQL queue depth and write growth.

A source error must not make the public API unready.

## Rollback

Set:

~~~text
WarApi__Enabled=false
~~~

and restart/redeploy the Worker.

Disabling ingestion does not delete:

- source registry rows;
- collection history;
- Fetch/Payload evidence;
- parse runs;
- poll state.

Do not manually delete or rewrite evidence as a rollback mechanism.

Existing expired leases are reconciled by the M2 recovery path when ingestion is next enabled.

## Expansion

Only after Live-1 behaves normally under the conservative bootstrap profile may Live-2 and Live-3 be enabled explicitly.

Dev remains a separate opt-in provenance domain and must never be used as automatic fallback for a live shard.

## M4 handoff

M3 activation is not permission to increase cadence.

M4 owns the bounded 48–72 hour measurement and publication of collection-profile@1.
