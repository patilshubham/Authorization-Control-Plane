import { useEffect, useMemo, useState } from "react";
import { Segmented } from "../../components/primitives";
import { AppIcon } from "../../components/icons";
import {
  cloneNode,
  fromJson,
  makeNode,
  summarize,
  toJson,
  validate,
  type CtxEntry,
  type CtxNode,
  type ValueKind,
} from "./contextModel";

const KIND_OPTIONS: { value: ValueKind; label: string }[] = [
  { value: "string", label: "Text" },
  { value: "number", label: "Number" },
  { value: "boolean", label: "True / False" },
  { value: "null", label: "Null" },
  { value: "object", label: "Object" },
  { value: "array", label: "List" },
];

/**
 * Visual + JSON context editor for the simulator. The visual builder is the
 * primary experience; the Advanced JSON view stays two-way synchronised.
 */
export function ContextBuilder({
  value,
  onChange,
}: {
  value: CtxNode;
  onChange: (node: CtxNode) => void;
}) {
  const [mode, setMode] = useState<"visual" | "json">("visual");
  const [jsonText, setJsonText] = useState(() => toJson(value));
  const [jsonError, setJsonError] = useState<string | null>(null);

  // Refresh the JSON text from the tree whenever we (re)enter JSON mode.
  useEffect(() => {
    if (mode === "json") {
      setJsonText(toJson(value));
      setJsonError(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode]);

  const errors = useMemo(() => validate(value), [value]);

  const onJsonInput = (text: string) => {
    setJsonText(text);
    try {
      const tree = fromJson(text);
      setJsonError(null);
      onChange(tree);
    } catch (err) {
      setJsonError(err instanceof Error ? err.message : "Invalid JSON.");
    }
  };

  const copyJson = () => {
    void navigator.clipboard?.writeText(toJson(value));
  };

  return (
    <div className="ctx-builder">
      <div className="ctx-toolbar">
        <Segmented
          ariaLabel="Context editor mode"
          value={mode}
          onChange={(v) => setMode(v as "visual" | "json")}
          options={[
            { value: "visual", label: "Visual" },
            { value: "json", label: "Advanced JSON" },
          ]}
        />
        <span className="ctx-summary" title={summarize(value)}>
          {summarize(value)}
        </span>
        <button type="button" className="btn-ghost btn-sm" onClick={copyJson}>
          Copy JSON
        </button>
      </div>

      {mode === "visual" ? (
        <div className="ctx-visual">
          <ObjectEditor node={value} onChange={onChange} isRoot />
          {value.entries && value.entries.length === 0 && (
            <p className="ctx-help">
              Add attributes such as <code>department</code>,{" "}
              <code>region</code>, or <code>mfaSatisfied</code> that your
              policies evaluate. Values can be text, numbers, true/false, lists,
              or nested objects.
            </p>
          )}
        </div>
      ) : (
        <div className="ctx-json">
          <textarea
            className={`ctx-json-area ${jsonError ? "is-invalid" : ""}`}
            value={jsonText}
            spellCheck={false}
            onChange={(e) => onJsonInput(e.target.value)}
            aria-label="Context JSON"
            aria-invalid={!!jsonError}
          />
          {jsonError ? (
            <p className="field-error">{jsonError}</p>
          ) : (
            <p className="ctx-help">
              Edits here sync live with the visual builder.
            </p>
          )}
        </div>
      )}

      {errors.length > 0 && (
        <ul className="ctx-errors" role="alert">
          {errors.map((e, i) => (
            <li key={i}>
              <code>{e.path}</code> — {e.message}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function ObjectEditor({
  node,
  onChange,
  isRoot,
}: {
  node: CtxNode;
  onChange: (n: CtxNode) => void;
  isRoot?: boolean;
}) {
  const entries = node.entries ?? [];

  const setEntries = (next: CtxEntry[]) => onChange({ ...node, entries: next });
  const addEntry = () =>
    setEntries([
      ...entries,
      {
        id: `${node.id}-e-${entries.length}-${Math.random().toString(36).slice(2, 6)}`,
        key: "",
        value: makeNode("string"),
      },
    ]);
  const updateEntry = (id: string, patch: Partial<CtxEntry>) =>
    setEntries(entries.map((e) => (e.id === id ? { ...e, ...patch } : e)));
  const removeEntry = (id: string) =>
    setEntries(entries.filter((e) => e.id !== id));
  const duplicateEntry = (id: string) => {
    const src = entries.find((e) => e.id === id);
    if (!src) return;
    const copy: CtxEntry = {
      id: `${node.id}-e-dup-${Math.random().toString(36).slice(2, 6)}`,
      key: src.key ? `${src.key}_copy` : "",
      value: cloneNode(src.value),
    };
    const idx = entries.findIndex((e) => e.id === id);
    setEntries([...entries.slice(0, idx + 1), copy, ...entries.slice(idx + 1)]);
  };

  return (
    <div className={isRoot ? "ctx-object ctx-root" : "ctx-object"}>
      {entries.map((entry) => (
        <div key={entry.id} className="ctx-entry">
          <input
            className="ctx-key"
            value={entry.key}
            placeholder="attribute"
            aria-label="Attribute name"
            onChange={(e) => updateEntry(entry.id, { key: e.target.value })}
          />
          <span className="ctx-colon">:</span>
          <div className="ctx-entry-value">
            <ValueEditor
              node={entry.value}
              onChange={(v) => updateEntry(entry.id, { value: v })}
            />
          </div>
          <div className="ctx-row-actions">
            <button
              type="button"
              className="icon-btn"
              title="Duplicate"
              aria-label="Duplicate attribute"
              onClick={() => duplicateEntry(entry.id)}
            >
              <AppIcon name="copy" size={14} />
            </button>
            <button
              type="button"
              className="icon-btn danger"
              title="Remove"
              aria-label="Remove attribute"
              onClick={() => removeEntry(entry.id)}
            >
              ✕
            </button>
          </div>
        </div>
      ))}
      <button
        type="button"
        className="btn-ghost btn-sm ctx-add"
        onClick={addEntry}
      >
        + Add attribute
      </button>
    </div>
  );
}

function ArrayEditor({
  node,
  onChange,
}: {
  node: CtxNode;
  onChange: (n: CtxNode) => void;
}) {
  const items = node.items ?? [];
  const setItems = (next: CtxNode[]) => onChange({ ...node, items: next });
  const addItem = () => setItems([...items, makeNode("string")]);
  const updateItem = (id: string, v: CtxNode) =>
    setItems(items.map((it) => (it.id === id ? v : it)));
  const removeItem = (id: string) =>
    setItems(items.filter((it) => it.id !== id));
  const duplicateItem = (id: string) => {
    const idx = items.findIndex((it) => it.id === id);
    if (idx < 0) return;
    setItems([
      ...items.slice(0, idx + 1),
      cloneNode(items[idx]),
      ...items.slice(idx + 1),
    ]);
  };

  return (
    <div className="ctx-array">
      {items.map((item, i) => (
        <div key={item.id} className="ctx-item">
          <span className="ctx-index">{i}</span>
          <div className="ctx-entry-value">
            <ValueEditor node={item} onChange={(v) => updateItem(item.id, v)} />
          </div>
          <div className="ctx-row-actions">
            <button
              type="button"
              className="icon-btn"
              title="Duplicate"
              aria-label="Duplicate item"
              onClick={() => duplicateItem(item.id)}
            >
              <AppIcon name="copy" size={14} />
            </button>
            <button
              type="button"
              className="icon-btn danger"
              title="Remove"
              aria-label="Remove item"
              onClick={() => removeItem(item.id)}
            >
              ✕
            </button>
          </div>
        </div>
      ))}
      <button
        type="button"
        className="btn-ghost btn-sm ctx-add"
        onClick={addItem}
      >
        + Add item
      </button>
    </div>
  );
}

function ValueEditor({
  node,
  onChange,
}: {
  node: CtxNode;
  onChange: (n: CtxNode) => void;
}) {
  const changeKind = (kind: ValueKind) => {
    if (kind === node.kind) return;
    // Preserve scalar text where it makes sense when switching between text/number.
    const next = makeNode(kind);
    if (kind === "string" && node.kind === "number")
      next.stringValue = node.numberValue;
    if (kind === "number" && node.kind === "string")
      next.numberValue = node.stringValue?.trim() || "0";
    onChange(next);
  };

  return (
    <div className="ctx-value">
      <select
        className="ctx-type"
        value={node.kind}
        aria-label="Value type"
        onChange={(e) => changeKind(e.target.value as ValueKind)}
      >
        {KIND_OPTIONS.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>

      {node.kind === "string" && (
        <input
          className="ctx-scalar"
          value={node.stringValue ?? ""}
          placeholder="value"
          aria-label="Text value"
          onChange={(e) => onChange({ ...node, stringValue: e.target.value })}
        />
      )}
      {node.kind === "number" && (
        <input
          className="ctx-scalar"
          type="text"
          inputMode="decimal"
          value={node.numberValue ?? ""}
          placeholder="0"
          aria-label="Number value"
          aria-invalid={!Number.isFinite(Number(node.numberValue))}
          onChange={(e) => onChange({ ...node, numberValue: e.target.value })}
        />
      )}
      {node.kind === "boolean" && (
        <select
          className="ctx-scalar"
          value={node.boolValue ? "true" : "false"}
          aria-label="Boolean value"
          onChange={(e) =>
            onChange({ ...node, boolValue: e.target.value === "true" })
          }
        >
          <option value="true">true</option>
          <option value="false">false</option>
        </select>
      )}
      {node.kind === "null" && <span className="ctx-null">null</span>}
      {node.kind === "object" && (
        <ObjectEditor node={node} onChange={onChange} />
      )}
      {node.kind === "array" && <ArrayEditor node={node} onChange={onChange} />}
    </div>
  );
}
