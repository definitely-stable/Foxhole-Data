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
  [
    "warapi-map-quality-policy.schema.json",
    "warapi-map-quality-policy@2.json",
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

const expectedQualityV2RuleKeys = [
  ...expectedQualityV1RuleKeys,
  "source-version.regression",
  "source-version.gap",
  "source-last-updated.regression",
  "representation.near-empty",
  "representation.mass-disappearance",
  "representation.duplicate-occurrence",
  "ownership.restart-collapse",
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
const qualityDocuments = [
  documents.get("warapi-map-quality-policy@1.json"),
  documents.get("warapi-map-quality-policy@2.json"),
].filter(Boolean);

if (taxonomy) {
  const requireUnique = (values, label, version) => {
    if (new Set(values).size !== values.length) {
      failed = true;
      console.error(
        `${version} contains duplicate ${label}`
      );
    }
  };

  for (const quality of qualityDocuments) {
    if (quality.taxonomyVersion !== taxonomy.version) {
      failed = true;
      console.error(
        `${quality.version} taxonomyVersion must match the embedded taxonomy profile version`
      );
    }

    const expectedKeys =
      quality.version === "warapi-map-quality@1"
        ? expectedQualityV1RuleKeys
        : quality.version === "warapi-map-quality@2"
          ? expectedQualityV2RuleKeys
          : null;

    if (!expectedKeys) {
      failed = true;
      console.error(
        `Unsupported quality policy contract ${quality.version}`
      );
      continue;
    }

    const keys = quality.rules.map((rule) => rule.key);
    const ruleVersions = quality.rules.map(
      (rule) => rule.ruleVersion
    );
    const configurationVersions = quality.rules.map(
      (rule) => rule.configurationVersion
    );

    requireUnique(keys, "rule keys", quality.version);
    requireUnique(ruleVersions, "rule versions", quality.version);
    requireUnique(
      configurationVersions,
      "configuration versions",
      quality.version
    );

    const expected = new Set(expectedKeys);
    const actual = new Set(keys);

    if (
      expected.size !== actual.size ||
      [...expected].some((key) => !actual.has(key))
    ) {
      failed = true;
      console.error(
        `${quality.version} rule set does not match its frozen contract`
      );
    }

    for (const rule of quality.rules) {
      if (rule.ruleVersion !== `${rule.key}@1`) {
        failed = true;
        console.error(
          `${quality.version}/${rule.key} must use ruleVersion ${rule.key}@1`
        );
      }

      if (
        rule.configurationVersion !==
        `${rule.key}-config@1`
      ) {
        failed = true;
        console.error(
          `${quality.version}/${rule.key} must use configurationVersion ` +
          `${rule.key}-config@1`
        );
      }
    }
  }
}

if (failed) {
  process.exitCode = 1;
}
