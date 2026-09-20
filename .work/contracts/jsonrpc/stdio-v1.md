# Foxhole-Data stdio JSON-RPC v1 contract

Protocol version: 1.0

Wire:

- JSON-RPC 2.0
- UTF-8
- one compact JSON message per line
- stdin client -> server
- stdout server -> client
- stderr diagnostics only
- no batch messages in v1

Required lifecycle:

1. foxdata.initialize
2. normal requests/subscriptions
3. foxdata.shutdown
4. EOF or foxdata.exit

Initial requests:

- foxdata.meta.get
- foxdata.shards.list
- foxdata.war.current
- foxdata.war.get
- foxdata.war.list
- foxdata.war.summary
- foxdata.region.snapshot
- foxdata.objective.get
- foxdata.objective.history
- foxdata.changes.list
- foxdata.changes.subscribe
- foxdata.changes.unsubscribe
- foxdata.export.create
- foxdata.operation.get
- foxdata.shutdown

Notifications:

- foxdata.cancel
- foxdata.exit
- foxdata.subscription.event
- foxdata.subscription.overrun
- foxdata.progress

Application error codes are stable strings in error.data.code.

Subscription events MUST include a durable cursor that can be used to catch up through foxdata.changes.list after disconnect.
