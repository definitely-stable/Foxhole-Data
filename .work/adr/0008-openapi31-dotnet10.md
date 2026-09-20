# ADR-0008 — OpenAPI 3.1 on .NET 10

Status: accepted for v1.

OpenAPI 3.2.1 is the latest published specification as of 20 September 2026.

ASP.NET Core .NET 10 natively generates OpenAPI 3.1; native 3.2 generation starts with .NET 11.

Foxhole-Data therefore uses one canonical executable OpenAPI 3.1 contract and tracks 3.2.1 as an upgrade target.

Do not maintain an independent manually converted 3.2 copy.
