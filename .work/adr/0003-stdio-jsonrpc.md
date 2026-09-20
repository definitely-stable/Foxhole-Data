# ADR-0003 — JSON-RPC 2.0 over stdio

Status: accepted for v1.

Local machine RPC uses UTF-8 JSON-RPC 2.0 over stdin/stdout with one compact JSON message per line.

stdout is protocol-only. stderr is diagnostics.

Batch requests are excluded from v1.

Reason:

JSON-RPC is transport-agnostic and language-neutral. Newline-delimited stdio framing has a current, widely deployed precedent in modern developer tooling and is substantially simpler than introducing a second binary IDL for local subprocess use.
