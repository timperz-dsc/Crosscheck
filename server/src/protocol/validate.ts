import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Ajv2020, type ErrorObject, type ValidateFunction } from 'ajv/dist/2020.js';
import { PROTOCOL_VERSION, type Envelope } from './types.js';

/** protocol/schema at the repo root – the same files the C# bridge validates against. */
export const SCHEMA_DIR = resolve(dirname(fileURLToPath(import.meta.url)), '../../../protocol/schema');
const BASE = 'https://github.com/crosscheck/protocol/v1/';

export type ValidationResult =
  | { ok: true; message: Envelope }
  | { ok: false; code: 'invalid_message' | 'unsupported_version'; message: string; details: string[] };

export class ProtocolValidator {
  private readonly byType = new Map<string, ValidateFunction>();
  private readonly any: ValidateFunction;

  constructor(schemaDir = SCHEMA_DIR) {
    const ajv = new Ajv2020({ allErrors: false, strict: true, strictRequired: false, allowUnionTypes: true });
    const load = (file: string) => JSON.parse(readFileSync(file, 'utf8')) as { $id: string; title?: string };
    ajv.addSchema(load(join(schemaDir, 'common.schema.json')));
    const messagesDir = join(schemaDir, 'messages');
    const types: string[] = [];
    for (const file of readdirSync(messagesDir).filter((f) => f.endsWith('.schema.json'))) {
      const schema = load(join(messagesDir, file));
      ajv.addSchema(schema);
      types.push(file.replace('.schema.json', ''));
    }
    ajv.addSchema(load(join(schemaDir, 'message.schema.json')));
    for (const type of types) {
      const fn = ajv.getSchema(`${BASE}messages/${type}.schema.json`);
      if (!fn) throw new Error(`schema for ${type} did not compile`);
      this.byType.set(type, fn);
    }
    const any = ajv.getSchema(`${BASE}message.schema.json`);
    if (!any) throw new Error('message.schema.json did not compile');
    this.any = any;
  }

  get types(): string[] {
    return [...this.byType.keys()];
  }

  /** Validates a parsed message against the schema for its type. */
  validate(data: unknown): ValidationResult {
    if (typeof data !== 'object' || data === null || Array.isArray(data)) {
      return { ok: false, code: 'invalid_message', message: 'message must be a JSON object', details: [] };
    }
    const record = data as Record<string, unknown>;
    if (record.v !== PROTOCOL_VERSION) {
      return { ok: false, code: 'unsupported_version', message: `protocol version ${String(record.v)} is not supported, this server speaks v${PROTOCOL_VERSION}`, details: [] };
    }
    const type = typeof record.type === 'string' ? record.type : '';
    const fn = this.byType.get(type);
    if (!fn) return { ok: false, code: 'invalid_message', message: `unknown message type "${type}"`, details: [] };
    if (!fn(data)) {
      const details = formatErrors(fn.errors);
      return { ok: false, code: 'invalid_message', message: `${type}: ${details[0] ?? 'invalid'}`, details };
    }
    return { ok: true, message: data as Envelope };
  }

  /** Validates against message.schema.json (the combined schema). */
  validateAny(data: unknown): boolean {
    return this.any(data) as boolean;
  }
}

function formatErrors(errors: ErrorObject[] | null | undefined): string[] {
  return (errors ?? []).map((e) => `${e.instancePath || '/'}: ${e.message ?? 'invalid'}${e.params && 'additionalProperty' in e.params ? ` (${String(e.params.additionalProperty)})` : ''}`);
}

let shared: ProtocolValidator | undefined;
export function sharedValidator(): ProtocolValidator {
  shared ??= new ProtocolValidator();
  return shared;
}
