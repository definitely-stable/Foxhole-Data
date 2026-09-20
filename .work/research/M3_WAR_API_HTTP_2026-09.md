# M3 War API and HTTP research — 20 September 2026

Status: non-normative evidence for M3.

This snapshot records the external facts used by [M3_OFFICIAL_WAR_API_ADAPTER.md](../M3_OFFICIAL_WAR_API_ADAPTER.md).

## Official War API

Authority:
https://github.com/clapfoot/warapi

Latest repository commit observed during M3 planning:
26 May 2026.

The current README documents:

- HTTPS + JSON;
- Live-1, Live-2 and Live-3 roots;
- a separate Dev root whose data is non-final;
- GET /worldconquest/war;
- GET /worldconquest/maps;
- GET /worldconquest/warReport/:mapName;
- GET /worldconquest/maps/:mapName/static;
- GET /worldconquest/maps/:mapName/dynamic/public;
- ETag/If-None-Match support;
- explicit request to honor returned cache headers;
- war data may update every 60 seconds;
- report/dynamic data may update every 3 seconds;
- static data is intended to be requested rarely;
- no documented stable map-item/objective identifier;
- undocumented flag bits are internal and must not be relied upon.

M3 therefore exposes only those documented endpoint families as the stable official source contract.

## September 2026 live observation

On 20 September 2026, the Live-1 maps endpoint was reachable and returned 53 map names.

The observed list included:

- MarbanHollow;
- DeadLandsHex;
- TheFingersHex;
- RedRiverHex;
- StlicanShelfHex;
- PalantineBermHex;
- OnyxHex.

This is a point-in-time measurement, not a durable source guarantee.

Capacity implication at the conservative M3 60-second cadence:

~~~text
53 maps
* 2 high-frequency per-map endpoint families (report + dynamic)
* 1440 minutes/day
= 152,640 requests/shard/day

Across three live shards:
457,920 requests/day
before war/maps/static traffic
~~~

This is why M3 uses conservative cadence plus deterministic phase spreading and why M4 measures before any faster profile is accepted.

## Upstream issue evidence

### Issue #77 — static map changes

https://github.com/clapfoot/warapi/issues/77

Historical static map data changed during a World Conquest despite the documentation describing it as static.

Consequence:

- static is long-cadence conditional revalidation, not fetch-once immutable data.

### Issue #81 — dayOfWar desynchronization

https://github.com/clapfoot/warapi/issues/81

Historical region day counts reset/desynchronized.

Consequence:

- dayOfWar remains raw source data;
- it is not the canonical global war clock.

### Issue #89 — ETag not browser-exposed by CORS

https://github.com/clapfoot/warapi/issues/89

Still open during planning.

Consequence:

- FoxData server-side ingestion can use source ETags directly;
- future browser-facing FoxData APIs must expose their own ETag/selected metadata through CORS.

### Issue #92 / #120 — restart/transient partial state

https://github.com/clapfoot/warapi/issues/92
https://github.com/clapfoot/warapi/issues/120

These document dynamic-map restart behavior that produced partial data, mass NONE ownership and false apparent changes. Both were closed as fixed in March 2025.

Consequence:

- the condition is not claimed as an active defect;
- fixtures remain mandatory;
- M3 preserves/parses the data;
- M6 owns quality rejection/quarantine.

### Issue #105 — If-None-Match handling bug history

https://github.com/clapfoot/warapi/issues/105

A 2023 server bug showed malformed/non-scalar If-None-Match input could cause server errors. It was closed the same day.

Consequence:

- M3 sends one validated entity-tag header value;
- it does not serialize collection-shaped or reconstructed validator syntax.

### Issue #112 — viewDirection

https://github.com/clapfoot/warapi/issues/112

The field first appeared in Dev in 2023 and was discussed outside the README contract.

Consequence:

- DTOs are tolerant to additive properties;
- exact raw bytes remain the ultimate evidence.

### Issue #127 — Home Region capability asymmetry

https://github.com/clapfoot/warapi/issues/127

Home-region reports were observed while static/dynamic map data was not available.

Consequence:

- endpoint capability is not inferred solely from the fact that a map-like name exists;
- HomeRegionC/HomeRegionW are treated as documented source quirks, not normalized ordinary hexes.

### Issue #134 — map/API naming inconsistency

https://github.com/clapfoot/warapi/issues/134

Open since February 2026.

Examples include MarbanHollow without Hex and asset/API spelling/case differences around Deadlands/DeadLands.

Consequence:

- mapName is an opaque exact source identifier;
- M3 does not append Hex, change case or derive API names from assets;
- aliasing belongs to later reference/semantic helpers.

### Issue #137 — undocumented icon 97 and live additive field

https://github.com/clapfoot/warapi/issues/137

Open issue created 7 July 2026 and updated 29 August 2026.

It documents a Live-1 RedRiverHex dynamic item with:

~~~json
{
  "teamId": "COLONIALS",
  "iconType": 97,
  "x": 0.71438116,
  "y": 0.5286482,
  "flags": 0,
  "viewDirection": 0
}
~~~

At planning time, the README's icon list ends at 92.

Consequence:

- the documented icon list is not a closed runtime enum;
- icon 97 is a required forward-compatibility fixture;
- viewDirection is an additive-field fixture;
- raw numeric/icon/property values survive even when semantics are unknown.

### Issue #141 — next-war start feature request

https://github.com/clapfoot/warapi/issues/141

Open and updated 16 September 2026.

This is a feature request, not a documented endpoint.

Consequence:

- M3 must not promote requested/undocumented future endpoints into the source contract.

## .NET 10 HTTP transport research

Current FoxData baseline is .NET 10.0.12.

### SocketsHttpHandler can retry internally

The .NET 10 runtime implementation of SocketsHttpHandler contains internal retry paths for selected connection failures and protocol fallback.

Source:
https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ConnectionPool/HttpConnectionPool.cs

Consequence:

The M2 guarantee cannot honestly mean that one AttemptId maps to exactly one physical network request in every runtime failure mode.

The enforceable FoxData boundary is:

> one AttemptId authorizes at most one application-issued SendAsync.

No FoxData retry/hedging/redirect layer may cause a second application send for that AttemptId.

### Standard resilience pipeline includes retry

Microsoft.Extensions.Http.Resilience standard resilience options include retry in the standard pipeline.

Reference:
https://learn.microsoft.com/dotnet/api/microsoft.extensions.http.resilience.httpstandardresilienceoptions

Consequence:

M3 does not attach AddStandardResilienceHandler to the upstream client. Retry remains a durable M2/M3 orchestration decision.

### Automatic redirects default to enabled

SocketsHttpHandler.AllowAutoRedirect defaults to true.

Reference:
https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.allowautoredirect

Consequence:

M3 explicitly disables redirects. A redirect otherwise creates additional HTTP requests and can cross the fixed-source authority boundary.

### Automatic decompression changes request/response behavior

SocketsHttpHandler.AutomaticDecompression controls content decoding. Enabling decompression also adds Accept-Encoding.

Reference:
https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.automaticdecompression

Consequence:

M3 sets AutomaticDecompression=None so evidence hashing sees transport-exposed content bytes before content decoding.

### ResponseHeadersRead requires explicit body deadline

HttpCompletionOption.ResponseHeadersRead completes once headers are available. HttpClient.Timeout no longer bounds the subsequent body-read operation.

Reference:
https://learn.microsoft.com/dotnet/api/system.net.http.httpcompletionoption

Consequence:

M3 uses an explicit full exchange cancellation deadline that remains active while streaming the response body.

### TimeProvider-aware timeout support

CancellationTokenSource(TimeSpan, TimeProvider) is available on .NET 10.

Reference:
https://learn.microsoft.com/dotnet/api/system.threading.cancellationtokensource.-ctor

Consequence:

M3 can test timeout behavior deterministically without using wall-clock sleeps.

## HTTP standards

### RFC 9110 — validators

https://www.rfc-editor.org/rfc/rfc9110.html

If-None-Match uses weak comparison. Weak ETags are therefore valid cache validators.

Consequence:

- preserve W/ validators;
- do not convert source ETags into content hashes;
- validate syntax before replay.

### RFC 9111 — cache freshness

https://www.rfc-editor.org/rfc/rfc9111.html

Important points:

- freshness is explicit when max-age/s-maxage/Expires is supplied;
- current age includes Date/Age/response delay semantics;
- no-cache requires validation before reuse;
- heuristic freshness exists but is not required.

Consequence:

M3 policy@1 uses explicit freshness only. It does not invent Last-Modified heuristics before M4 measurements.

### Retry-After

RFC 9110 permits Retry-After as either an HTTP-date or delay-seconds.

Consequence:

M3 treats valid Retry-After as a lower bound for successor eligibility.

## System.Text.Json

References:

https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation
https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation-modes

Source generation supports metadata-driven serialization/deserialization. JsonExtensionData is compatible with metadata mode; it is not a reason to require fast-path-only generation.

Consequence:

M3 uses generated metadata context, not a fast-path-only assumption. Unknown/additive fields are captured through JsonExtensionData while exact raw JSON remains immutable evidence.

## Community implementation signal

ThePhoenix78/FoxAPI was observed at version 2.1.1 / commit 1 June 2026.

Repository:
https://github.com/ThePhoenix78/FoxAPI

It independently demonstrates continued demand for:

- ETag-aware requests;
- cache-aware calls;
- async access;
- typed object models;
- higher-level region helpers.

FoxData deliberately does not copy wrapper architecture. Its differentiator is durable multi-shard evidence, versioned source interpretation, historical reconstruction and language-neutral outputs.

## Research conclusions

M3 should not be a thin HttpClient wrapper.

The September 2026 evidence supports:

- fixed official roots;
- exact shard provenance;
- cache-aware conditional GET;
- one FoxData application send per durable AttemptId;
- no automatic redirect/retry/hedging layer;
- bounded streaming;
- exact pre-content-decoding evidence bytes;
- open source values and additive-field tolerance;
- source-shape fingerprinting;
- opaque map identifiers;
- durable validator/scheduling state separate from M2 fencing;
- deterministic request spreading;
- offline deterministic tests;
- non-blocking live canary only after the adapter is complete.
