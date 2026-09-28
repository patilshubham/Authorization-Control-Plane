import { useMemo, useState } from "react";
import { SlideOver, Field } from "../components/primitives";
import {
  useCreateReferenceData,
  useUpdateReferenceData,
} from "../api/hooks";
import { userFacingError } from "../apiClient";
import { useToast } from "../components/Toast";
import type { ReferenceDataSummary } from "../types";

// A reference-data value must be a JSON array or object — scalars are rejected so
// policy conditions can meaningfully traverse or membership-test the document.
function validateValue(text: string): string | null {
  if (!text.trim()) return "Value is required.";
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return "Value must be valid JSON.";
  }
  if (parsed === null || typeof parsed !== "object") {
    return "Value must be a JSON array or object.";
  }
  return null;
}

// A key must be a dotted identifier so it can be addressed as reference.<key> in
// condition expressions (e.g. reference.allowed_countries).
function validateKey(key: string): string | null {
  if (!key.trim()) return "Key is required.";
  if (!/^[A-Za-z][A-Za-z0-9_.-]*$/.test(key.trim())) {
    return "Use letters, digits, dot, dash or underscore; start with a letter.";
  }
  return null;
}

/**
 * Create/edit form for an application's reference-data documents. The key is immutable
 * once created (it is the address used by <c>reference.&lt;key&gt;</c> in policies), so it
 * is only editable in create mode.
 */
export function ReferenceDataForm({
  appId,
  existing,
  onClose,
}: {
  appId: string;
  existing?: ReferenceDataSummary;
  onClose: () => void;
}) {
  const isEdit = !!existing;
  const create = useCreateReferenceData(appId);
  const update = useUpdateReferenceData(appId);
  const toast = useToast();
  const [key, setKey] = useState(existing?.key ?? "");
  const [description, setDescription] = useState(existing?.description ?? "");
  const [value, setValue] = useState(() =>
    prettyOrRaw(existing?.value ?? "[]"),
  );

  const keyError = useMemo(() => (isEdit ? null : validateKey(key)), [key, isEdit]);
  const valueError = useMemo(() => validateValue(value), [value]);
  const pending = create.isPending || update.isPending;
  const canSubmit = !keyError && !valueError && !pending;

  const format = () => setValue((v) => prettyOrRaw(v));

  const submit = () => {
    if (!canSubmit) return;
    const onSuccess = () => onClose();
    const onError = (err: unknown) => toast.error(userFacingError(err));
    if (isEdit) {
      update.mutate(
        { key: existing!.key, description: description.trim() || undefined, value },
        { onSuccess, onError },
      );
    } else {
      create.mutate(
        { key: key.trim(), description: description.trim() || undefined, value },
        { onSuccess, onError },
      );
    }
  };

  return (
    <SlideOver
      open
      title={isEdit ? `Edit ${existing!.key}` : "New reference data"}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="btn-primary"
            disabled={!canSubmit}
            onClick={submit}
          >
            {pending ? "Saving…" : isEdit ? "Save changes" : "Create"}
          </button>
        </>
      }
    >
      <Field
        label="Key"
        required
        hint={
          isEdit
            ? "Immutable — referenced by policies as reference.<key>."
            : "Referenced by policies as reference.<key> (e.g. allowed_countries)."
        }
      >
        <input
          value={key}
          placeholder="allowed_countries"
          readOnly={isEdit}
          disabled={isEdit}
          onChange={(e) => setKey(e.target.value)}
          aria-required="true"
        />
        {keyError && <p className="field-error">{keyError}</p>}
      </Field>
      <Field label="Description" hint="Optional — what this document is for.">
        <input
          value={description}
          placeholder="ISO country codes cleared for access"
          onChange={(e) => setDescription(e.target.value)}
        />
      </Field>
      <Field
        label="Value (JSON)"
        required
        hint="A JSON array or object. Policies read it via reference.<key> — arrays support in / contains-all; objects support dotted paths."
      >
        <div className="refdata-value-editor">
          <div className="refdata-value-toolbar">
            <button
              type="button"
              className="mini-btn ghost"
              onClick={format}
              disabled={!!valueError}
              title="Pretty-print JSON"
            >
              Format
            </button>
          </div>
          <textarea
            className={`refdata-value-input ${valueError ? "has-error" : ""}`}
            value={value}
            spellCheck={false}
            rows={12}
            onChange={(e) => setValue(e.target.value)}
          />
          {valueError ? (
            <p className="field-error">{valueError}</p>
          ) : (
            <p className="field-ok">Valid JSON.</p>
          )}
        </div>
      </Field>
    </SlideOver>
  );
}

// Pretty-print JSON when parseable; otherwise return the original text unchanged
// so the user can keep editing malformed input without losing it.
function prettyOrRaw(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}
