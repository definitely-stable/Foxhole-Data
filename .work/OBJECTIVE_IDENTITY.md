# Objective identity

The official source does not document a stable map-item/objective ID.

## Identity layers

1. Source occurrence — one item in one raw representation.
2. Within-war objective instance.
3. Optional cross-war canonical objective.
4. Objective revision/alias metadata.

Do not hash x, y and iconType and call it a permanent objective ID.

## Matching evidence

A matcher MAY use:

- same war and region;
- coordinate distance;
- compatible icon family/taxonomy;
- static-map evidence;
- nearby text marker;
- previous accepted instance;
- source map revision context.

Each run persists:

- matcher version;
- parameters;
- input references;
- candidates;
- feature values;
- scores;
- best/second-best score;
- ambiguity margin;
- decision.

Decision states:

- accepted_auto
- accepted_manual
- ambiguous
- unmatched
- rejected
- superseded

## Safety rule

If confidence or ambiguity margin is insufficient, canonicalObjectiveId remains null.

Ambiguity is valid data, not a parser failure.

Manual overrides are append-only audited decisions and can be superseded/reverted without deleting prior evidence.
