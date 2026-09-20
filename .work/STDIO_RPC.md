# stdio RPC protocol v1

Status: design contract.

Transport: JSON-RPC 2.0 over UTF-8 stdin/stdout.

The protocol is intentionally small and transport-specific. Domain objects are shared conceptually with the canonical public API, but the wire shape is versioned independently.

## Process model

- client launches FoxData.Cli with: foxdata rpc --stdio
- client writes requests/notifications to stdin;
- server writes responses/notifications to stdout;
- server may write UTF-8 diagnostics to stderr;
- stdout MUST contain protocol frames only;
- client MUST NOT send non-protocol content on stdin.

## Framing

v1 uses newline-delimited compact JSON-RPC messages.

- one complete JSON-RPC message per line;
- no embedded raw newline in a frame;
- UTF-8 only;
- maximum inbound and outbound frame size is negotiated at initialization and bounded by server configuration.

This mirrors a widely deployed modern stdio RPC pattern and avoids Content-Length framing complexity for v1.

Batch JSON-RPC requests are NOT supported in v1. Concurrency is achieved with independent request IDs.

## Lifecycle

First request MUST be:

foxdata.initialize

Parameters:

~~~json
{
  "protocolVersion": "1.0",
  "client": {"name": "example", "version": "1.2.3"},
  "capabilities": {
    "subscriptions": true,
    "progress": true
  }
}
~~~

Result includes:

- negotiated protocolVersion
- server version
- capability list
- maximum frame bytes
- supported output schema versions

Shutdown:

1. client sends foxdata.shutdown request;
2. server stops accepting new work and returns success;
3. client closes stdin or sends foxdata.exit notification;
4. server exits.

EOF before shutdown is treated as client disconnect, not protocol corruption.

## Core methods

Initial method namespace:

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

## Streaming

Subscriptions produce server notifications:

foxdata.subscription.event

Each notification contains:

- subscriptionId
- durable change cursor
- event ID
- event payload

A client that reconnects MUST resume from the durable cursor through foxdata.changes.list. stdio notifications are a convenience stream, not the history authority.

## Cancellation

v1 defines foxdata.cancel notification with a request ID.

Cancellation is cooperative. A disconnect is NOT equivalent to cancellation.

## Backpressure

Each connection has bounded outbound buffering.

If a subscriber cannot keep up:

- non-durable convenience notifications may be stopped;
- server emits a terminal subscription-overrun notification when possible;
- client resumes through durable cursor queries.

The server MUST NOT allow unbounded stdout buffering.

## Errors

JSON-RPC protocol errors use standard JSON-RPC error structure.

Domain errors use stable application error codes inside error.data, including:

- not_found
- invalid_argument
- invalid_cursor
- unsupported_protocol
- busy
- operation_failed

Stack traces are never sent.

## Security boundary

stdio is local subprocess IPC. v1 performs no independent user authentication by default. The trust boundary is the OS process/user boundary.

Secrets MUST NOT be logged to stderr.
