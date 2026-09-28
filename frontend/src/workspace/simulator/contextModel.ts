// Visual context model for the Authorization Simulator.
//
// The runtime engine accepts an arbitrary JSON object as the request `context`
// (IReadOnlyDictionary<string, object?>). Policy conditions reference these
// attributes (e.g. `context.department`). This model lets administrators build
// that object visually — no JSON knowledge required — while staying a faithful,
// loss-free representation of any JSON value the backend can consume.

export type ValueKind =
  "string" | "number" | "boolean" | "null" | "object" | "array";

export interface CtxNode {
  id: string;
  kind: ValueKind;
  stringValue?: string;
  /** Kept as raw text so partial input (e.g. "-", "1.") is editable; parsed on serialize. */
  numberValue?: string;
  boolValue?: boolean;
  entries?: CtxEntry[];
  items?: CtxNode[];
}

export interface CtxEntry {
  id: string;
  key: string;
  value: CtxNode;
}

let counter = 0;
export function newId(): string {
  counter += 1;
  return `ctx-${counter}-${Math.random().toString(36).slice(2, 8)}`;
}

export function makeNode(kind: ValueKind): CtxNode {
  switch (kind) {
    case "string":
      return { id: newId(), kind, stringValue: "" };
    case "number":
      return { id: newId(), kind, numberValue: "0" };
    case "boolean":
      return { id: newId(), kind, boolValue: true };
    case "null":
      return { id: newId(), kind };
    case "object":
      return { id: newId(), kind, entries: [] };
    case "array":
      return { id: newId(), kind, items: [] };
  }
}

export function emptyContext(): CtxNode {
  return makeNode("object");
}

/** Deep clone with fresh ids (used by "duplicate"). */
export function cloneNode(node: CtxNode): CtxNode {
  return {
    ...node,
    id: newId(),
    entries: node.entries?.map((e) => ({
      id: newId(),
      key: e.key,
      value: cloneNode(e.value),
    })),
    items: node.items?.map((i) => cloneNode(i)),
  };
}

function serializeNode(node: CtxNode): unknown {
  switch (node.kind) {
    case "string":
      return node.stringValue ?? "";
    case "number": {
      const n = Number(node.numberValue);
      return Number.isFinite(n) ? n : 0;
    }
    case "boolean":
      return node.boolValue ?? false;
    case "null":
      return null;
    case "object": {
      const obj: Record<string, unknown> = {};
      for (const entry of node.entries ?? []) {
        if (entry.key.trim()) obj[entry.key] = serializeNode(entry.value);
      }
      return obj;
    }
    case "array":
      return (node.items ?? []).map(serializeNode);
  }
}

export function serializeContext(root: CtxNode): Record<string, unknown> {
  return serializeNode(root) as Record<string, unknown>;
}

export function toJson(root: CtxNode): string {
  return JSON.stringify(serializeContext(root), null, 2);
}

function parseValue(value: unknown): CtxNode {
  if (value === null) return makeNode("null");
  if (Array.isArray(value)) {
    return { id: newId(), kind: "array", items: value.map(parseValue) };
  }
  switch (typeof value) {
    case "string":
      return { id: newId(), kind: "string", stringValue: value };
    case "number":
      return { id: newId(), kind: "number", numberValue: String(value) };
    case "boolean":
      return { id: newId(), kind: "boolean", boolValue: value };
    case "object": {
      const entries: CtxEntry[] = Object.entries(
        value as Record<string, unknown>,
      ).map(([key, v]) => ({
        id: newId(),
        key,
        value: parseValue(v),
      }));
      return { id: newId(), kind: "object", entries };
    }
    default:
      return makeNode("null");
  }
}

/** Parse a JSON string into a context tree. Throws if not a JSON object. */
export function fromJson(json: string): CtxNode {
  const trimmed = json.trim();
  if (!trimmed) return emptyContext();
  const parsed = JSON.parse(trimmed);
  if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error('Context must be a JSON object (e.g. { "key": "value" }).');
  }
  return parseValue(parsed);
}

export interface CtxError {
  path: string;
  message: string;
}

/** Collect human-readable validation errors (empty/duplicate keys, invalid numbers). */
export function validate(root: CtxNode): CtxError[] {
  const errors: CtxError[] = [];
  const walk = (node: CtxNode, path: string) => {
    if (node.kind === "object") {
      const seen = new Set<string>();
      for (const entry of node.entries ?? []) {
        const key = entry.key.trim();
        const here = path ? `${path}.${key || "?"}` : key || "?";
        if (!key) errors.push({ path: here, message: "Key name is required." });
        else if (seen.has(key))
          errors.push({ path: here, message: `Duplicate key “${key}”.` });
        seen.add(key);
        walk(entry.value, here);
      }
    } else if (node.kind === "array") {
      (node.items ?? []).forEach((item, i) => walk(item, `${path}[${i}]`));
    } else if (node.kind === "number") {
      if (!Number.isFinite(Number(node.numberValue))) {
        errors.push({ path: path || "value", message: "Not a valid number." });
      }
    }
  };
  walk(root, "");
  return errors;
}

/** Short human-readable summary of the root object. */
export function summarize(root: CtxNode): string {
  const entries = root.entries ?? [];
  if (entries.length === 0)
    return "No context attributes — the decision uses roles and policies only.";
  const named = entries.filter((e) => e.key.trim()).map((e) => e.key.trim());
  const preview = named.slice(0, 4).join(", ");
  const rest = named.length > 4 ? ` +${named.length - 4} more` : "";
  return `${named.length} attribute${named.length === 1 ? "" : "s"}: ${preview}${rest}`;
}
