import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";

const contractsRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function full(p) {
  return path.join(contractsRoot, p);
}

async function readJson(relPath) {
  const raw = await fs.readFile(full(relPath), "utf8");
  return JSON.parse(raw);
}

const index = await readJson("schema-index.json");
const schemaRelPaths = Array.from(new Set([
  ...index.entries.map((e) => e.schema),
  "shared/common-defs.schema.json"
]));

const schemas = [];
for (const rel of schemaRelPaths) {
  const schema = await readJson(rel);
  schemas.push({ rel, schema });
}

for (const { rel, schema } of schemas) {
  if (rel === "shared/common-defs.schema.json") {
    continue;
  }

  const props = schema.properties ?? {};
  const required = schema.required ?? [];

  if (props.contract_name?.const === undefined) {
    throw new Error(`${rel}: properties.contract_name.const is required`);
  }

  if (props.schema_version?.const === undefined) {
    throw new Error(`${rel}: properties.schema_version.const is required`);
  }

  if (!required.includes("contract_name") || !required.includes("schema_version")) {
    throw new Error(`${rel}: required[] must include contract_name and schema_version`);
  }
}

const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);

for (const { schema } of schemas) {
  ajv.addSchema(schema);
}

for (const entry of index.entries) {
  const schema = await readJson(entry.schema);
  const example = await readJson(entry.example);

  const validate = ajv.getSchema(schema.$id);
  if (!validate) {
    throw new Error(`Missing compiled schema for ${entry.schema}`);
  }
  const ok = validate(example);
  if (!ok) {
    const errors = (validate.errors ?? [])
      .map((e) => `${e.instancePath || "/"} ${e.message}`)
      .join("; ");
    throw new Error(`Example validation failed for ${entry.example}: ${errors}`);
  }
}

console.log(`Validated ${schemaRelPaths.length} schemas and ${index.entries.length} examples.`);
