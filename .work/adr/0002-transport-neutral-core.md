# ADR-0002 — Transport-neutral application semantics

Status: accepted.

HTTP, stdio and any future gRPC/IPC adapters call the same Application query/command handlers.

No domain operation may require HttpContext, JSON-RPC request objects, protobuf messages or transport-specific exceptions.

Reason:

Foxhole-Data is intended for developers using different languages and environments. Transport-neutral semantics keep the product interoperable and testable and avoid duplicated business logic.
