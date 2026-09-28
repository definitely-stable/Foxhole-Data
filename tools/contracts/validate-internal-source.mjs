import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const here = path.dirname(fileURLToPath(import.meta.url));
const contracts = path.resolve(
  here,
  "../../.work/contracts/internal/source"
);

const pairs = [
  [
    "warapi-map-taxonomy.schema.json",
    "warapi-map-taxonomy@1.json",
  ],
  [
    "warapi-map-quality-policy.schema.json",
    "warapi-map-quality-policy@1.json",
  ],
];

const expectedQualityV1RuleKeys = [
  "region-id.valid",
  "region-id.conflict",
  "coordinate.valid",
  "source-time.representable",
  "schema.structure-changed",
  "taxonomy.unknown-icon",
  "taxonomy.unknown-team",
  "taxonomy.unknown-flag-bits",
];

const ajv = new Ajv2020({
  allErrors: true,
  strict: true,
});
addFormats(ajv);

let failed = false;
const documents = new Map();

for (const [schemaName, documentName] of pairs) {
  const schema = JSON.parse(
    fs.readFileSync(path.join(contracts, schemaName), "utf8")
  );
  const document = JSON.parse(
    fs.readFileSync(path.join(contracts, documentName), "utf8")
  );
  const validate = ajv.compile(schema);
  documents.set(documentName, document);

  if (!validate(document)) {
    failed = true;
    console.error(
      `${documentName} does not satisfy ${schemaName}`
    );
    for (const error of validate.errors ?? []) {
      console.error(
        `  ${error.instancePath || "/"} ${error.message ?? "invalid"}`
      );
    }
  }
}

const taxonomy = documents.get(
  "warapi-map-taxonomy@1.json"
);
const quality = documents.get(
  "warapi-map-quality-policy@1.json"
);

if (taxonomy && quality) {
  if (quality.taxonomyVersion !== taxonomy.version) {
    failed = true;
    console.error(
      "warapi-map-quality-policy@1.json taxonomyVersion " +
      "must match the embedded taxonomy profile version"
    );
  }

  const keys = quality.rules.map((rule) => rule.key);
  const ruleVersions = quality.rules.map(
    (rule) => rule.ruleVersion
  );
  const configurationVersions = quality.rules.map(
    (rule) => rule.configurationVersion
  );

  const requireUnique = (values, label) => {
    if (new Set(values).size !== values.length) {
      failed = true;
      console.error(
        `warapi-map-quality-policy@1.json contains duplicate ${label}`
      );
    }
  };

  requireUnique(keys, "rule keys");
  requireUnique(ruleVersions, "rule versions");
  requireUnique(
    configurationVersions,
    "configuration versions"
  );

  const expected = new Set(expectedQualityV1RuleKeys);
  const actual = new Set(keys);

  if (
    expected.size !== actual.size ||
    [...expected].some((key) => !actual.has(key))
  ) {
    failed = true;
    console.error(
      "warapi-map-quality-policy@1.json must contain exactly " +
      "the M6-E1 structural rule set"
    );
  }

  for (const rule of quality.rules) {
    if (rule.ruleVersion !== `${rule.key}@1`) {
      failed = true;
      console.error(
        `${rule.key} must use ruleVersion ${rule.key}@1`
      );
    }

    if (
      rule.configurationVersion !==
      `${rule.key}-config@1`
    ) {
      failed = true;
      console.error(
        `${rule.key} must use configurationVersion ` +
        `${rule.key}-config@1`
      );
    }
  }
}

if (failed) {
  process.exitCode = 1;
}
