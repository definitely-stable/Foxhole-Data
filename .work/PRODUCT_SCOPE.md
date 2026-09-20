# Product scope

Foxhole-Data is an independent, language-neutral developer data platform for public Foxhole data.

Its job is to convert upstream state-oriented sources into reliable evidence, normalized canonical data, historical observations and stable developer-facing outputs.

## v1 product capabilities

Foxhole-Data MUST provide:

- official World Conquest War API collection for Live-1, Live-2 and Live-3;
- explicit isolation of Dev data from live data;
- conditional source requests using source cache headers and ETags;
- immutable source evidence and provenance;
- canonical wars, regions, map observations, reports and objectives;
- quality classification and anomaly quarantine;
- coverage information, so missing data is never confused with unchanged state;
- historical state reconstruction;
- observed changes with uncertainty windows instead of invented exact event times;
- a canonical public HTTP API;
- a source-shaped War API compatibility facade;
- first-class local stdio RPC;
- a durable cursor change feed;
- SSE for low-latency remote streaming;
- signed outbound webhooks;
- asynchronous bulk exports;
- generated transport SDKs and separate semantic helper SDKs.

## v1 non-goals

The following are deliberately outside the mandatory v1:

- private or faction-only intelligence;
- player tracking;
- winner prediction;
- tactical automation;
- GraphQL as the primary public API;
- mandatory gRPC;
- mandatory Unix domain sockets or Windows Named Pipes;
- Redis, Kafka, RabbitMQ, NATS, Kubernetes or a search cluster without measured need;
- exposing undocumented upstream endpoints as stable guarantees;
- coupling runtime-war availability to asset/reference-data availability.

## Extension boundary

Future adapters MAY add public assets, map media, item/reference catalogues, technology-tree data or other public sources. They MUST enter through the same evidence, normalization, quality and provenance architecture rather than bypassing it.
