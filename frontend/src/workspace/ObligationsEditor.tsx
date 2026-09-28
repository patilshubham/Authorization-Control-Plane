import { useEffect, useMemo, useRef, useState } from "react";
import { AppIcon } from "../components/icons";

// Shape of a single obligation as authored in the UI.
type ObligationRow = { id: string; value: string };

// Parse the stored JSON array into editable rows. Accepts either bare id strings
// (["require_mfa"]) or objects ({ id, value }). Malformed input yields no rows so
// the editor degrades gracefully instead of throwing.
function parseObligations(json: string | undefined): ObligationRow[] {
  if (!json || !json.trim()) return [];
  try {
    const parsed = JSON.parse(json);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map((entry): ObligationRow | null => {
        if (typeof entry === "string") return { id: entry, value: "" };
        if (entry && typeof entry === "object" && typeof entry.id === "string") {
          return {
            id: entry.id,
            value:
              entry.value == null
                ? ""
                : typeof entry.value === "string"
                  ? entry.value
                  : String(entry.value),
          };
        }
        return null;
      })
      .filter((row): row is ObligationRow => row !== null);
  } catch {
    return [];
  }
}

// Serialize rows back to the backend contract. Rows with a value become objects,
// value-less rows collapse to bare id strings, and empty ids are dropped.
function serializeObligations(rows: ObligationRow[]): string {
  const cleaned = rows
    .map((row) => ({ id: row.id.trim(), value: row.value.trim() }))
    .filter((row) => row.id.length > 0)
    .map((row) => (row.value ? { id: row.id, value: row.value } : row.id));
  return JSON.stringify(cleaned);
}

/**
 * Editor for a policy's obligations — advisory instructions (e.g. <c>require_mfa</c>)
 * returned to the calling application when the policy contributes to a decision. Emits
 * the backend-contract JSON array on every change.
 */
export function ObligationsEditor({
  value,
  onChange,
}: {
  value: string;
  onChange: (json: string) => void;
}) {
  // Rows are held locally so an in-progress empty row (e.g. just after "Add
  // obligation") stays visible even though it serializes away — a purely derived
  // model would make the Add button appear to do nothing. We re-sync only when the
  // incoming `value` differs from what we last emitted (external reset / edit seed).
  const [rows, setRows] = useState<ObligationRow[]>(() =>
    parseObligations(value),
  );
  const lastEmitted = useRef(value);

  useEffect(() => {
    if (value !== lastEmitted.current) {
      setRows(parseObligations(value));
      lastEmitted.current = value;
    }
  }, [value]);

  function commit(next: ObligationRow[]) {
    setRows(next);
    const json = serializeObligations(next);
    lastEmitted.current = json;
    onChange(json);
  }

  function updateRow(index: number, patch: Partial<ObligationRow>) {
    commit(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  }

  function addRow() {
    commit([...rows, { id: "", value: "" }]);
  }

  function removeRow(index: number) {
    commit(rows.filter((_, i) => i !== index));
  }

  const duplicateIds = useMemo(() => {
    const seen = new Set<string>();
    const dupes = new Set<string>();
    for (const row of rows) {
      const id = row.id.trim().toLowerCase();
      if (!id) continue;
      if (seen.has(id)) dupes.add(id);
      seen.add(id);
    }
    return dupes;
  }, [rows]);

  return (
    <div className="obligations-editor">
      {rows.length === 0 ? (
        <p className="obligations-empty">
          No obligations. Add advisory instructions the calling app must enforce.
        </p>
      ) : (
        <ul className="obligations-list">
          {rows.map((row, index) => {
            const isDupe = duplicateIds.has(row.id.trim().toLowerCase());
            return (
              <li key={index} className="obligation-row">
                <input
                  className={`obligation-id ${isDupe ? "has-error" : ""}`}
                  value={row.id}
                  placeholder="require_mfa"
                  aria-label="Obligation id"
                  onChange={(e) => updateRow(index, { id: e.target.value })}
                />
                <input
                  className="obligation-value"
                  value={row.value}
                  placeholder="value (optional)"
                  aria-label="Obligation value"
                  onChange={(e) => updateRow(index, { value: e.target.value })}
                />
                <button
                  type="button"
                  className="icon-btn danger"
                  title="Remove obligation"
                  aria-label="Remove obligation"
                  onClick={() => removeRow(index)}
                >
                  ✕
                </button>
              </li>
            );
          })}
        </ul>
      )}
      {duplicateIds.size > 0 && (
        <p className="obligations-error">Obligation ids must be unique.</p>
      )}
      <button type="button" className="mini-btn" onClick={addRow}>
        <AppIcon name="plus" size={14} /> Add obligation
      </button>
    </div>
  );
}
