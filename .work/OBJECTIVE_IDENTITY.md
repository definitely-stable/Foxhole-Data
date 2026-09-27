# Objective identity

The official source does not document a stable map-item/objective ID.

## Identity layers

1. Source occurrence — one item in one raw representation.
2. Within-war objective instance.
3. Optional cross-war canonical objective.
4. Objective revision/alias metadata.

Do not hash x, y and iconType and call it a permanent objective ID.

M6 owns only layer 1. A source occurrence is scoped to one exact normalized map representation and retains a representation-local source ordinal for provenance. The ordinal is not stable across representations.

M7 begins at layer 2 and consumes only M6 observations allowed by the selected quality policy. Suspect/quarantined M6 representations remain evidence but are not automatic identity inputs.

Exact duplicate source occurrences are preserved. Upstream War API behavior has demonstrated duplicate map entries, so equality of coordinates/icon/team/flags is not proof that two occurrences are one objective.

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
