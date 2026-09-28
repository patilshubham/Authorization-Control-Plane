// Condition model — the seam between the visual builder and the backend JSON contract.
//
// Backend contract (Authorization.Infrastructure EfAuthorizationPolicyEngine):
//   { "match": "all"|"any", "conditions": [ <node>, ... ] }
//   node = leaf   { "attribute": "context.x", "operator": "eq", "value": "..." }
//        | group  { "match": "all"|"any", "conditions": [ <node>, ... ] }
//   "all" => every child must match (AND). "any" => at least one child (OR).
//   attribute/value resolve `context.<key>` and `assignment.<key>` references; any other
//   value string is a literal. `in`/`notIn` values are comma-separated lists.
//
// The visual builder never asks the user to write this JSON by hand; this module converts
// between the UI tree and the exact backend JSON in both directions.

export type Combinator = "all" | "any" | "none";

export type OperatorId =
  | "eq"
  | "neq"
  | "gt"
  | "gte"
  | "lt"
  | "lte"
  | "contains"
  | "notContains"
  | "startsWith"
  | "endsWith"
  | "matches"
  | "notMatches"
  | "in"
  | "notIn"
  | "containsAny"
  | "containsAll"
  | "before"
  | "after"
  | "between"
  | "exists"
  | "notExists"
  | "isTrue"
  | "isFalse";

/** How the value input for an operator should behave. */
export type ValueArity = "single" | "number" | "list" | "range" | "none";

export type OperatorMeta = {
  id: OperatorId;
  label: string;
  symbol: string;
  arity: ValueArity;
  group: "compare" | "text" | "set" | "datetime" | "presence";
  hint: string;
};

export const OPERATORS: readonly OperatorMeta[] = [
  {
    id: "eq",
    label: "equals",
    symbol: "=",
    arity: "single",
    group: "compare",
    hint: "Attribute value matches exactly (case-insensitive).",
  },
  {
    id: "neq",
    label: "does not equal",
    symbol: "≠",
    arity: "single",
    group: "compare",
    hint: "Attribute value differs.",
  },
  {
    id: "gt",
    label: "greater than",
    symbol: ">",
    arity: "number",
    group: "compare",
    hint: "Numeric or date comparison.",
  },
  {
    id: "gte",
    label: "greater than or equal",
    symbol: "≥",
    arity: "number",
    group: "compare",
    hint: "Numeric or date comparison.",
  },
  {
    id: "lt",
    label: "less than",
    symbol: "<",
    arity: "number",
    group: "compare",
    hint: "Numeric or date comparison.",
  },
  {
    id: "lte",
    label: "less than or equal",
    symbol: "≤",
    arity: "number",
    group: "compare",
    hint: "Numeric or date comparison.",
  },
  {
    id: "contains",
    label: "contains",
    symbol: "⊃",
    arity: "single",
    group: "text",
    hint: "Attribute text contains the value.",
  },
  {
    id: "notContains",
    label: "does not contain",
    symbol: "⊅",
    arity: "single",
    group: "text",
    hint: "Attribute text does not contain the value.",
  },
  {
    id: "startsWith",
    label: "starts with",
    symbol: "⌜",
    arity: "single",
    group: "text",
    hint: "Attribute text begins with the value.",
  },
  {
    id: "endsWith",
    label: "ends with",
    symbol: "⌟",
    arity: "single",
    group: "text",
    hint: "Attribute text ends with the value.",
  },
  {
    id: "matches",
    label: "matches regex",
    symbol: "~",
    arity: "single",
    group: "text",
    hint: "Attribute text matches the regular expression.",
  },
  {
    id: "notMatches",
    label: "does not match regex",
    symbol: "≁",
    arity: "single",
    group: "text",
    hint: "Attribute text does not match the regular expression.",
  },
  {
    id: "in",
    label: "is any of",
    symbol: "∈",
    arity: "list",
    group: "set",
    hint: "Attribute value is one of a comma-separated list.",
  },
  {
    id: "notIn",
    label: "is none of",
    symbol: "∉",
    arity: "list",
    group: "set",
    hint: "Attribute value is not in a comma-separated list.",
  },
  {
    id: "containsAny",
    label: "contains any of",
    symbol: "⋒",
    arity: "list",
    group: "set",
    hint: "Attribute is a list sharing at least one value with the list.",
  },
  {
    id: "containsAll",
    label: "contains all of",
    symbol: "⊇",
    arity: "list",
    group: "set",
    hint: "Attribute is a list containing every value in the list.",
  },
  {
    id: "before",
    label: "before (date)",
    symbol: "⏰",
    arity: "single",
    group: "datetime",
    hint: "Attribute date/time is earlier than the value.",
  },
  {
    id: "after",
    label: "after (date)",
    symbol: "⏱",
    arity: "single",
    group: "datetime",
    hint: "Attribute date/time is later than the value.",
  },
  {
    id: "between",
    label: "between (inclusive)",
    symbol: "↔",
    arity: "range",
    group: "datetime",
    hint: "Attribute is within an inclusive min–max range (numbers or dates).",
  },
  {
    id: "exists",
    label: "is present",
    symbol: "∃",
    arity: "none",
    group: "presence",
    hint: "Attribute has a non-empty value.",
  },
  {
    id: "notExists",
    label: "is absent",
    symbol: "∄",
    arity: "none",
    group: "presence",
    hint: "Attribute is empty or missing.",
  },
  {
    id: "isTrue",
    label: "is true",
    symbol: "✓",
    arity: "none",
    group: "presence",
    hint: "Attribute is a truthy boolean (true/1/yes).",
  },
  {
    id: "isFalse",
    label: "is false",
    symbol: "✗",
    arity: "none",
    group: "presence",
    hint: "Attribute is a falsy boolean (false/0/no).",
  },
] as const;

export const OPERATOR_BY_ID: Record<OperatorId, OperatorMeta> =
  Object.fromEntries(OPERATORS.map((o) => [o.id, o])) as Record<
    OperatorId,
    OperatorMeta
  >;

export function operatorArity(id: OperatorId): ValueArity {
  return OPERATOR_BY_ID[id]?.arity ?? "single";
}

// ── UI tree ─────────────────────────────────────────────────────────────────

export type RuleNode = {
  id: string;
  kind: "rule";
  attribute: string;
  operator: OperatorId;
  value: string;
};

export type GroupNode = {
  id: string;
  kind: "group";
  combinator: Combinator;
  children: ConditionNode[];
};

export type ConditionNode = RuleNode | GroupNode;

let counter = 0;
export function nextId(prefix = "n"): string {
  counter += 1;
  const rand =
    typeof crypto !== "undefined" && "randomUUID" in crypto
      ? crypto.randomUUID().slice(0, 8)
      : String(Math.random()).slice(2, 10);
  return `${prefix}-${counter}-${rand}`;
}

export function newRule(): RuleNode {
  return {
    id: nextId("r"),
    kind: "rule",
    attribute: "context.",
    operator: "eq",
    value: "",
  };
}

export function newGroup(combinator: Combinator = "all"): GroupNode {
  return { id: nextId("g"), kind: "group", combinator, children: [newRule()] };
}

export function emptyRoot(): GroupNode {
  return { id: nextId("g"), kind: "group", combinator: "all", children: [] };
}

// ── Value helpers (literal vs. reference) ─────────────────────────────────────

export type ValueMode = "literal" | "context" | "assignment" | "system" | "reference";

export function valueMode(value: string): ValueMode {
  if (value.startsWith("context.")) return "context";
  if (value.startsWith("assignment.")) return "assignment";
  if (value.startsWith("system.")) return "system";
  if (value.startsWith("reference.")) return "reference";
  return "literal";
}

export function valueKey(value: string): string {
  const mode = valueMode(value);
  if (mode === "context") return value.slice("context.".length);
  if (mode === "assignment") return value.slice("assignment.".length);
  if (mode === "system") return value.slice("system.".length);
  if (mode === "reference") return value.slice("reference.".length);
  return value;
}

export function composeValue(mode: ValueMode, key: string): string {
  if (mode === "context") return `context.${key}`;
  if (mode === "assignment") return `assignment.${key}`;
  if (mode === "system") return `system.${key}`;
  if (mode === "reference") return `reference.${key}`;
  return key;
}

// ── Serialization: UI tree → backend JSON ─────────────────────────────────────

type SerializedLeaf = { attribute: string; operator: string; value: string };
type SerializedGroup = {
  match: Combinator;
  conditions: Array<SerializedLeaf | SerializedGroup>;
};

function serializeNode(node: ConditionNode): SerializedLeaf | SerializedGroup {
  if (node.kind === "group") {
    return {
      match: node.combinator,
      conditions: node.children.map(serializeNode),
    };
  }
  return {
    attribute: node.attribute.trim(),
    operator: node.operator,
    value: node.value,
  };
}

export function serialize(root: GroupNode): string {
  const payload = serializeNode(root) as SerializedGroup;
  return JSON.stringify(payload, null, 2);
}

// ── Deserialization: backend JSON → UI tree ───────────────────────────────────

function isRecord(x: unknown): x is Record<string, unknown> {
  return typeof x === "object" && x !== null && !Array.isArray(x);
}

function parseNode(raw: unknown): ConditionNode | null {
  if (!isRecord(raw)) return null;

  if (Array.isArray(raw.conditions)) {
    const combinator: Combinator =
      raw.match === "any" ? "any" : raw.match === "none" ? "none" : "all";
    const children = raw.conditions
      .map(parseNode)
      .filter((n): n is ConditionNode => n !== null);
    return { id: nextId("g"), kind: "group", combinator, children };
  }

  if (typeof raw.operator === "string") {
    const operator = (
      OPERATOR_BY_ID[raw.operator as OperatorId] ? raw.operator : "eq"
    ) as OperatorId;
    const attribute = typeof raw.attribute === "string" ? raw.attribute : "";
    let value = "";
    if (typeof raw.value === "string") value = raw.value;
    else if (raw.value !== undefined && raw.value !== null)
      value = String(raw.value);
    return { id: nextId("r"), kind: "rule", attribute, operator, value };
  }

  return null;
}

/** Parse a backend conditions JSON string into a UI tree. Returns an empty root on failure. */
export function deserialize(json: string | undefined | null): {
  root: GroupNode;
  error: string | null;
} {
  if (!json || !json.trim()) return { root: emptyRoot(), error: null };
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return { root: emptyRoot(), error: "Not valid JSON." };
  }
  if (!isRecord(parsed) || !Array.isArray(parsed.conditions)) {
    return {
      root: emptyRoot(),
      error: "Expected an object with a conditions array.",
    };
  }
  const node = parseNode(parsed);
  if (!node || node.kind !== "group") {
    return { root: emptyRoot(), error: "Unrecognised condition structure." };
  }
  return { root: node, error: null };
}

// ── Human-readable summary ────────────────────────────────────────────────────

function humanValue(value: string, arity: ValueArity): string {
  if (arity === "none") return "";
  const mode = valueMode(value);
  if (mode !== "literal") return value; // reference, show as-is
  if (arity === "list") {
    const items = value
      .split(",")
      .map((s) => s.trim())
      .filter(Boolean);
    return items.length ? `[${items.join(", ")}]` : "[…]";
  }
  if (arity === "range") {
    const bounds = value
      .split(",")
      .map((s) => s.trim())
      .filter(Boolean);
    return bounds.length === 2 ? `${bounds[0]} … ${bounds[1]}` : "… … …";
  }
  return value === "" ? "…" : arity === "number" ? value : `“${value}”`;
}

function summarizeNode(node: ConditionNode, depth: number): string {
  if (node.kind === "rule") {
    const meta = OPERATOR_BY_ID[node.operator];
    const attr = node.attribute.trim() || "attribute";
    if (!meta) return `${attr} ${node.operator} ${node.value}`;
    if (meta.arity === "none") return `${attr} ${meta.label}`;
    return `${attr} ${meta.label} ${humanValue(node.value, meta.arity)}`.trim();
  }
  if (node.children.length === 0) return "(no conditions)";
  const joiner = node.combinator === "all" ? " AND " : " OR ";
  const parts = node.children.map((child) => summarizeNode(child, depth + 1));
  const joined = parts.join(joiner);
  if (node.combinator === "none") return `NOT (${joined})`;
  return depth === 0 ? joined : `(${joined})`;
}

/** A plain-English description of the whole condition tree. */
export function summarize(root: GroupNode): string {
  if (root.children.length === 0) return "Always applies (no conditions).";
  return summarizeNode(root, 0);
}

// ── Validation ────────────────────────────────────────────────────────────────

export type ValidationIssue = { nodeId: string; message: string };

function validateNode(
  node: ConditionNode,
  issues: ValidationIssue[],
  isRoot: boolean,
): void {
  if (node.kind === "group") {
    // An empty root group means "Always applies (no conditions)" — a valid state.
    // Only nested groups must contain at least one condition.
    if (node.children.length === 0 && !isRoot) {
      issues.push({
        nodeId: node.id,
        message: "Group needs at least one condition.",
      });
    }
    node.children.forEach((child) => validateNode(child, issues, false));
    return;
  }

  const attr = node.attribute.trim();
  if (!attr) {
    issues.push({ nodeId: node.id, message: "Choose an attribute." });
  } else if (
    !attr.startsWith("context.") &&
    !attr.startsWith("assignment.") &&
    !attr.startsWith("system.")
  ) {
    issues.push({
      nodeId: node.id,
      message: "Attribute must start with context., assignment., or system.",
    });
  } else if (
    attr === "context." ||
    attr === "assignment." ||
    attr === "system."
  ) {
    issues.push({
      nodeId: node.id,
      message: "Attribute is missing a key after the prefix.",
    });
  }

  const arity = operatorArity(node.operator);
  if (arity !== "none") {
    if (valueMode(node.value) === "literal") {
      const v = node.value.trim();
      if (!v) {
        issues.push({ nodeId: node.id, message: "Enter a value." });
      } else if (arity === "number" && Number.isNaN(Number(v))) {
        issues.push({ nodeId: node.id, message: "Value must be a number." });
      } else if (
        arity === "list" &&
        v
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean).length === 0
      ) {
        issues.push({
          nodeId: node.id,
          message: "Enter at least one list value.",
        });
      } else if (
        arity === "range" &&
        v
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean).length !== 2
      ) {
        issues.push({
          nodeId: node.id,
          message: "Enter exactly two bounds (min, max).",
        });
      }
    } else {
      // reference — must have a key after the prefix
      if (!valueKey(node.value).trim()) {
        issues.push({
          nodeId: node.id,
          message: "Reference is missing a key.",
        });
      }
    }
  }
}

export function validate(root: GroupNode): ValidationIssue[] {
  const issues: ValidationIssue[] = [];
  validateNode(root, issues, true);
  return issues;
}

/** Count leaf rules in the tree (for headers / empty-state logic). */
export function countRules(node: ConditionNode): number {
  if (node.kind === "rule") return 1;
  return node.children.reduce((sum, child) => sum + countRules(child), 0);
}

// ── Tree editing (immutable helpers) ──────────────────────────────────────────

export function mapTree(
  node: ConditionNode,
  fn: (n: ConditionNode) => ConditionNode,
): ConditionNode {
  const mapped = fn(node);
  if (mapped.kind === "group") {
    return {
      ...mapped,
      children: mapped.children.map((child) => mapTree(child, fn)),
    };
  }
  return mapped;
}

export function updateNode(
  root: GroupNode,
  id: string,
  patch: (n: ConditionNode) => ConditionNode,
): GroupNode {
  return mapTree(root, (n) => (n.id === id ? patch(n) : n)) as GroupNode;
}

export function insertChild(
  root: GroupNode,
  groupId: string,
  child: ConditionNode,
): GroupNode {
  return mapTree(root, (n) =>
    n.kind === "group" && n.id === groupId
      ? { ...n, children: [...n.children, child] }
      : n,
  ) as GroupNode;
}

export function removeNode(root: GroupNode, id: string): GroupNode {
  return mapTree(root, (n) =>
    n.kind === "group"
      ? { ...n, children: n.children.filter((c) => c.id !== id) }
      : n,
  ) as GroupNode;
}

export function duplicateNode(node: ConditionNode): ConditionNode {
  if (node.kind === "group") {
    return {
      ...node,
      id: nextId("g"),
      children: node.children.map(duplicateNode),
    };
  }
  return { ...node, id: nextId("r") };
}

export function duplicateInto(
  root: GroupNode,
  groupId: string,
  sourceId: string,
): GroupNode {
  let clone: ConditionNode | null = null;
  const find = (n: ConditionNode) => {
    if (n.id === sourceId) clone = duplicateNode(n);
    if (n.kind === "group") n.children.forEach(find);
  };
  find(root);
  if (!clone) return root;
  return insertChild(root, groupId, clone);
}

/** Move a child within a group from one index to another (drag reordering). */
export function reorderChild(
  root: GroupNode,
  groupId: string,
  from: number,
  to: number,
): GroupNode {
  return mapTree(root, (n) => {
    if (n.kind !== "group" || n.id !== groupId) return n;
    const children = [...n.children];
    if (from < 0 || from >= children.length || to < 0 || to >= children.length)
      return n;
    const [moved] = children.splice(from, 1);
    children.splice(to, 0, moved);
    return { ...n, children };
  }) as GroupNode;
}
