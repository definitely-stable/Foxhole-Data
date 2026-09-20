# ADR-0009 — Standard Webhooks-compatible signing

Status: accepted direction for public webhook v1.

Webhook signing/verification SHOULD follow Standard Webhooks 1.0 conventions where compatible with the CloudEvents payload model.

Reason:

Standard Webhooks has active reference libraries across major languages and reduces the cost and security risk for consumers compared with a Foxhole-Data-specific signing scheme.

Delivery durability, filtering, retries and replay remain Foxhole-Data product behavior.
