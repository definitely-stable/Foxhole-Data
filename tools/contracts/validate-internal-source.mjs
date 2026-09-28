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
];

const ajv = new Ajv2020({
  allErrors: true,
  strict: true,
});
addFormats(ajv);

let failed = false;

for (const [schemaName, documentName] of pairs) {
  const schema = JSON.parse(
    fs.readFileSync(path.join(contracts, schemaName), "utf8")
  );
  const document = JSON.parse(
    fs.readFileSync(path.join(contracts, documentName), "utf8")
  );
  const validate = ajv.compile(schema);

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

if (failed) {
  process.exitCode = 1;
}
