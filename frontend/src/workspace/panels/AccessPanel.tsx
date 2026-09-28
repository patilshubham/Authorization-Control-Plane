import { useEffect, useMemo, useState } from "react";
import {
  useAssignments,
  useAssignmentsPaged,
  useAssignmentsSummary,
  useBreakGlass,
  useCreateAssignment,
  useImportAssignments,
  useRevokeAssignment,
  useUpdateAssignment,
  useRoles,
} from "../../api/hooks";
import type { AssignmentSummary } from "../../types";
import {
  portalApi,
  userFacingError,
  type AssignmentImportRowInput,
} from "../../apiClient";
import { Field, SlideOver, Spinner } from "../../components/primitives";
import { AppIcon } from "../../components/icons";
import { DataTable, StatusChip, usePageSizeState, type DataTableColumn } from "../../ui";
import { useToast } from "../../components/Toast";
import { useCapabilities } from "../../capabilities";
import { displayState } from "../formatters";
import { ASSIGNMENT_STATES } from "../../constants";
import { DonutChart, useChartTheme } from "../../components/charts";
import { VizModal } from "../../components/viz/VizModal";
import {
  D3BipartiteGraph,
  type BipartiteNode,
  type BipartiteLink,
} from "../../components/viz/D3BipartiteGraph";

// Human-readable label for each CSV-import row outcome (CREATE / UPDATE / SKIP / ERROR).
const IMPORT_ACTION_LABELS: Record<string, string> = {
  CREATE: "Create",
  UPDATE: "Update",
  SKIP: "Skip",
  ERROR: "Error",
};

// ── Assignment expiry helpers (derived from real backend data, never fabricated) ─

/** Human label + tone for an expiry timestamp, e.g. "in 5 days" / "Expired 2 days ago". */
function expiryLabel(validUntil: string | null): {
  text: string;
  tone: "muted" | "warning" | "danger";
} {
  if (!validUntil) return { text: "No expiry", tone: "muted" };
  const target = new Date(validUntil).getTime();
  const diffMs = target - Date.now();
  const day = 86_400_000;
  const abs = Math.abs(diffMs);
  const unit =
    abs >= day
      ? `${Math.round(abs / day)} day${Math.round(abs / day) === 1 ? "" : "s"}`
      : abs >= 3_600_000
        ? `${Math.round(abs / 3_600_000)}h`
        : `${Math.max(1, Math.round(abs / 60_000))}m`;
  if (diffMs <= 0) return { text: `Expired ${unit} ago`, tone: "danger" };
  if (diffMs <= 7 * day) return { text: `Expires in ${unit}`, tone: "warning" };
  return { text: `Expires in ${unit}`, tone: "muted" };
}
/** Convert an ISO timestamp to the yyyy-mm-dd value an <input type="date"> expects. */
function toDateInput(iso: string | null): string {
  return iso ? new Date(iso).toISOString().slice(0, 10) : "";
}

/** Convert a date-input value (yyyy-mm-dd) to an end-of-day ISO string for the API. */
function fromDateInput(value: string): string {
  return new Date(`${value}T23:59:59`).toISOString();
}

// ── CSV import parsing (client-side; the server validates every row) ─────────────

/** Splits one CSV line, honouring double-quoted fields and escaped quotes. */
function splitCsvLine(line: string): string[] {
  const out: string[] = [];
  let cur = "";
  let inQuotes = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (inQuotes) {
      if (ch === '"') {
        if (line[i + 1] === '"') {
          cur += '"';
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        cur += ch;
      }
    } else if (ch === '"') {
      inQuotes = true;
    } else if (ch === ",") {
      out.push(cur);
      cur = "";
    } else {
      cur += ch;
    }
  }
  out.push(cur);
  return out;
}

/**
 * Parses an assignments CSV into structured rows. Accepts a header row
 * (subjectEmail,roleKey,validUntil) in any column order, or falls back to fixed
 * column order when no recognised header is present. A malformed validUntil is
 * normalised to null so a single bad date never fails the whole batch — the row
 * is then validated server-side like any other.
 */
function parseAssignmentsCsv(text: string): AssignmentImportRowInput[] {
  const lines = text.split(/\r?\n/).filter((line) => line.trim().length > 0);
  if (lines.length === 0) return [];
  const header = splitCsvLine(lines[0]).map((h) => h.trim().toLowerCase());
  const emailIdx = header.indexOf("subjectemail");
  const roleIdx = header.indexOf("rolekey");
  const untilIdx = header.indexOf("validuntil");
  const hasHeader = emailIdx >= 0 && roleIdx >= 0;
  const eIdx = hasHeader ? emailIdx : 0;
  const rIdx = hasHeader ? roleIdx : 1;
  const uIdx = hasHeader ? untilIdx : 2;
  const rows: AssignmentImportRowInput[] = [];
  for (let i = hasHeader ? 1 : 0; i < lines.length; i++) {
    const cells = splitCsvLine(lines[i]);
    const subjectEmail = (cells[eIdx] ?? "").trim();
    const roleKey = (cells[rIdx] ?? "").trim();
    const rawUntil = uIdx >= 0 ? (cells[uIdx] ?? "").trim() : "";
    let validUntil: string | null = null;
    if (rawUntil) {
      const parsed = new Date(rawUntil);
      validUntil = Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
    }
    rows.push({ subjectEmail, roleKey, validUntil });
  }
  return rows;
}

// ── Assignments ────────────────────────────────────────────────────────────────

export function AccessPanel({ appId }: { appId: string }) {
  const roles = useRoles(appId);
  const revoke = useRevokeAssignment(appId);
  const create = useCreateAssignment(appId);
  const updateAssignment = useUpdateAssignment(appId);
  const importAssignments = useImportAssignments(appId);
  const breakGlass = useBreakGlass(appId);
  const toast = useToast();
  const canAssignRoles = useCapabilities().can("AssignRoles", appId);
  const chart = useChartTheme();

  const [open, setOpen] = useState(false);
  const [subjectEmail, setSubjectEmail] = useState("");
  const [roleKey, setRoleKey] = useState("");
  const [validUntil, setValidUntil] = useState("");
  const [stateFilter, setStateFilter] = useState("");
  const [expiry, setExpiry] = useState("");
  const [q, setQ] = useState("");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  const [graphOpen, setGraphOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [importRows, setImportRows] = useState<AssignmentImportRowInput[]>([]);
  const [importFileName, setImportFileName] = useState("");
  const [exporting, setExporting] = useState(false);
  // Break-glass (emergency) grant — always time-boxed and reason-gated.
  const [bgOpen, setBgOpen] = useState(false);
  const [bgSubject, setBgSubject] = useState("");
  const [bgRole, setBgRole] = useState("");
  const [bgReason, setBgReason] = useState("");
  const [bgHours, setBgHours] = useState(4);
  useEffect(() => setPage(1), [q, stateFilter, expiry, pageSize]);

  // Table: server-paginated + server-filtered current page.
  const assignments = useAssignmentsPaged(appId, {
    page,
    pageSize,
    q: q || undefined,
    state: stateFilter || undefined,
    expiry: expiry || undefined,
  });
  // Donut/total: lightweight aggregate that never depends on the current page.
  const summary = useAssignmentsSummary(appId);
  // Bipartite graph needs every grant; fetched lazily only while the modal is open.
  const graphAssignments = useAssignments(appId, graphOpen);

  // Edit slide-over — role, expiry and metadata can all be changed in place.
  const [editing, setEditing] = useState<AssignmentSummary | null>(null);
  const [editRole, setEditRole] = useState("");
  const [editUntil, setEditUntil] = useState("");
  const [editReason, setEditReason] = useState("");

  const resetForm = () => {
    setSubjectEmail("");
    setRoleKey("");
    setValidUntil("");
  };

  // Privileged is always derived from the selected role — never a separately-editable field —
  // so it can never drift from the role's own authoritative Privileged flag.
  const selectedRoleObj = (roles.data ?? []).find((r) => r.roleKey === roleKey);
  const privileged = !!selectedRoleObj?.privileged;
  const expiryMissing = privileged && !validUntil;
  const canSubmit =
    !!subjectEmail && !!roleKey && !expiryMissing && !create.isPending;

  const submit = () =>
    create.mutate(
      {
        subjectEmail,
        roleKey,
        validUntil: validUntil ? fromDateInput(validUntil) : null,
      },
      {
        onSuccess: () => {
          setOpen(false);
          resetForm();
        },
      },
    );

  const openEdit = (a: AssignmentSummary) => {
    setEditing(a);
    setEditRole(a.roleKey);
    setEditUntil(toDateInput(a.validUntil));
    setEditReason(a.reason ?? "");
  };

  const editRoleObj = (roles.data ?? []).find((r) => r.roleKey === editRole);
  const editExpiryMissing = !!editRoleObj?.privileged && !editUntil;
  const canSaveEdit =
    !!editing?.id &&
    !!editRole &&
    !editExpiryMissing &&
    !updateAssignment.isPending;

  const saveEdit = () => {
    if (!editing?.id || !editRole) return;
    updateAssignment.mutate(
      {
        assignmentId: editing.id,
        roleKey: editRole,
        validUntil: editUntil ? fromDateInput(editUntil) : null,
        reason: editReason || undefined,
      },
      { onSuccess: () => setEditing(null) },
    );
  };

  const importResult = importAssignments.data;

  const onImportFile = async (file: File | undefined) => {
    if (!file) return;
    setImportFileName(file.name);
    try {
      const text = await file.text();
      const parsed = parseAssignmentsCsv(text);
      setImportRows(parsed);
      if (parsed.length === 0) {
        toast.error("No rows found in the file.");
        return;
      }
      importAssignments.mutate({ dryRun: true, rows: parsed });
    } catch {
      toast.error("Could not read the file.");
    }
  };

  const applyImport = () => {
    if (importRows.length === 0) return;
    importAssignments.mutate(
      { dryRun: false, rows: importRows },
      {
        onSuccess: (result) => {
          if (!result.dryRun) {
            const parts: string[] = [];
            if (result.created) parts.push(`${result.created} created`);
            if (result.updated) parts.push(`${result.updated} updated`);
            if (result.skipped) parts.push(`${result.skipped} skipped`);
            toast.success(
              parts.length
                ? `Import complete — ${parts.join(", ")}.`
                : "Import complete — no changes were needed.",
            );
          }
        },
        onError: (e) => toast.error(userFacingError(e)),
      },
    );
  };

  const resetImport = () => {
    setImportOpen(false);
    setImportRows([]);
    setImportFileName("");
    importAssignments.reset();
  };

  const exportCsv = async () => {
    if (exporting) return;
    setExporting(true);
    try {
      const fileName = await portalApi.exportAssignmentsCsv(appId);
      toast.success(
        `Exported ${fileName}. If the download doesn't open, check your browser's downloads.`,
      );
    } catch (error) {
      toast.error(userFacingError(error));
    } finally {
      setExporting(false);
    }
  };

  const canBreakGlass =
    !!bgSubject.trim() &&
    !!bgRole &&
    !!bgReason.trim() &&
    bgHours >= 1 &&
    bgHours <= 24 &&
    !breakGlass.isPending;

  const resetBreakGlass = () => {
    setBgOpen(false);
    setBgSubject("");
    setBgRole("");
    setBgReason("");
    setBgHours(4);
  };

  const submitBreakGlass = () => {
    if (!canBreakGlass) return;
    breakGlass.mutate(
      {
        subjectEmail: bgSubject.trim(),
        roleKey: bgRole,
        reason: bgReason.trim(),
        durationHours: bgHours,
      },
      {
        onSuccess: resetBreakGlass,
        onError: (e) => toast.error(userFacingError(e)),
      },
    );
  };

  const columns: DataTableColumn<AssignmentSummary>[] = [
    {
      key: "subject",
      header: "Subject",
      sortValue: (a) => a.subjectEmail,
      searchValue: (a) => a.subjectEmail,
      render: (a) => <span className="cell-strong">{a.subjectEmail}</span>,
    },
    {
      key: "role",
      header: "Role",
      sortValue: (a) => a.roleKey,
      searchValue: (a) => a.roleKey,
    },
    {
      key: "state",
      header: "State",
      sortValue: (a) => displayState(a),
      searchValue: (a) => displayState(a),
      render: (a) => <StatusChip value={displayState(a)} />,
    },
    {
      key: "valid",
      header: "Expires",
      sortValue: (a) => a.validUntil ?? "",
      render: (a) => {
        const label = expiryLabel(a.validUntil);
        return (
          <span className={`expiry expiry-${label.tone}`}>{label.text}</span>
        );
      },
    },
  ];

  const rows = assignments.data?.items ?? [];

  const stateDonut = useMemo(() => {
    const colorFor = (s: string) => {
      const u = s.toUpperCase();
      if (u === "ACTIVE") return chart.success;
      if (u === "EXPIRED") return chart.warning;
      if (u === "REVOKED") return chart.danger;
      return chart.muted;
    };
    return (summary.data?.states ?? [])
      .slice()
      .sort((a, b) => a.state.localeCompare(b.state))
      .map(({ state, count }) => ({
        label: state,
        value: count,
        color: colorFor(state),
      }));
  }, [summary.data, chart]);
  const totalAssignments = summary.data?.total ?? 0;

  const graphData = useMemo(() => {
    const list = graphAssignments.data ?? [];
    const subjects = new Map<string, BipartiteNode>();
    const roleNodes = new Map<string, BipartiteNode>();
    const links: BipartiteLink[] = [];
    for (const a of list) {
      if (!subjects.has(a.subjectEmail))
        subjects.set(a.subjectEmail, {
          id: a.subjectEmail,
          label: a.subjectEmail,
          kind: "application",
        });
      if (!roleNodes.has(a.roleKey)) {
        const r = (roles.data ?? []).find((x) => x.roleKey === a.roleKey);
        roleNodes.set(a.roleKey, {
          id: a.roleKey,
          label: r?.name ?? a.roleKey,
          sublabel: a.roleKey,
          kind: "role",
          riskLevel: r?.riskLevel,
        });
      }
      const st = displayState(a).toLowerCase();
      const state: BipartiteLink["state"] =
        st === "active"
          ? "active"
          : st === "expired"
            ? "expired"
            : st === "revoked"
              ? "revoked"
              : undefined;
      links.push({ source: a.subjectEmail, target: a.roleKey, state });
    }
    return {
      left: Array.from(subjects.values()),
      right: Array.from(roleNodes.values()),
      links,
    };
  }, [graphAssignments.data, roles.data]);

  return (
    <>
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Assignments</h1>
          <p className="page-sub">
            Grants that give a subject a role in this application, optionally
            time-boxed.
          </p>
        </div>
        <div className="page-head-actions">
          <button
            type="button"
            className="btn-secondary"
            disabled={totalAssignments === 0}
            onClick={() => setGraphOpen(true)}
          >
            Assignment graph
          </button>
          <button
            type="button"
            className="btn-secondary"
            disabled={totalAssignments === 0 || exporting}
            aria-busy={exporting || undefined}
            onClick={exportCsv}
          >
            {exporting ? "Exporting…" : "Export CSV"}
          </button>
          {canAssignRoles && (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setImportOpen(true)}
            >
              Import
            </button>
          )}
          {canAssignRoles && (
            <button
              type="button"
              className="btn-danger"
              onClick={() => {
                resetBreakGlass();
                setBgOpen(true);
              }}
              title="Grant short-lived emergency access"
            >
              Break-glass
            </button>
          )}
          {canAssignRoles && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => {
                resetForm();
                setOpen(true);
              }}
            >
              Grant access
            </button>
          )}
        </div>
      </header>

      {totalAssignments > 0 && (
        <section className="panel access-donut-panel">
          <div className="access-donut-chart">
            <DonutChart
              data={stateDonut}
              size={150}
              centerLabel={String(totalAssignments)}
              centerSub="grants"
            />
          </div>
          <ul className="access-donut-legend">
            {stateDonut.map((d) => (
              <li key={d.label}>
                <span
                  className="donut-swatch"
                  style={{ background: d.color }}
                  aria-hidden="true"
                />
                <span className="donut-label">{d.label}</span>
                <span className="donut-value">{d.value}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <DataTable
        columns={columns}
        rows={rows}
        getRowKey={(a, i) => a.id ?? `${a.subjectEmail}-${a.roleKey}-${i}`}
        isLoading={assignments.isLoading}
        searchPlaceholder="Search subjects or roles…"
        searchValue={q}
        onSearchChange={setQ}
        filters={[
          {
            key: "state",
            label: "State",
            value: stateFilter,
            onChange: setStateFilter,
            options: [
              { value: "", label: "All states" },
              ...ASSIGNMENT_STATES.map((s) => ({ value: s, label: s })),
            ],
          },
          {
            key: "expiry",
            label: "Expiry",
            value: expiry,
            onChange: setExpiry,
            options: [
              { value: "", label: "All expiries" },
              { value: "expired", label: "Expired" },
              { value: "soon", label: "Expiring in 7 days" },
              { value: "later", label: "Expires later" },
              { value: "none", label: "No expiry" },
            ],
          },
        ]}
        onClearFilters={() => {
          setStateFilter("");
          setExpiry("");
        }}
        serverPagination={{
          page,
          pageSize,
          total: assignments.data?.total ?? 0,
          onPageChange: setPage,
          onPageSizeChange: setPageSize,
        }}
        emptyMessage="No assignments yet. Grant a role to a subject to get started."
        emptyAction={
          canAssignRoles
            ? {
                label: "Grant access",
                onClick: () => {
                  resetForm();
                  setOpen(true);
                },
              }
            : undefined
        }
        rowActions={(a) =>
          a.id && canAssignRoles ? (
            <div className="row-action-group">
              <button
                type="button"
                className="mini-btn"
                onClick={() => openEdit(a)}
              >
                Edit
              </button>
              <button
                type="button"
                className="mini-btn danger"
                disabled={revoke.isPending}
                onClick={() => revoke.mutate(a.id!)}
              >
                Revoke
              </button>
            </div>
          ) : null
        }
      />

      <SlideOver
        open={open}
        title="Grant access"
        onClose={() => setOpen(false)}
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setOpen(false)}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              disabled={!canSubmit}
              onClick={submit}
            >
              {create.isPending ? "Granting…" : "Grant"}
            </button>
          </>
        }
      >
        <Field label="Subject email" required>
          <input
            value={subjectEmail}
            onChange={(e) => setSubjectEmail(e.target.value)}
            placeholder="user@company.com"
            aria-required="true"
          />
        </Field>
        <Field label="Role" required>
          <select
            value={roleKey}
            onChange={(e) => setRoleKey(e.target.value)}
            aria-required="true"
          >
            <option value="">Select a role…</option>
            {(roles.data ?? []).map((r) => (
              <option key={r.roleKey} value={r.roleKey}>
                {r.name}
              </option>
            ))}
          </select>
        </Field>
        <Field
          label="Expires on"
          required={privileged}
          hint={
            privileged
              ? "Required — privileged access must be time-boxed."
              : "Optional — leave empty for permanent access."
          }
          error={
            expiryMissing
              ? "Privileged assignments require an expiry date."
              : undefined
          }
        >
          <input
            type="date"
            value={validUntil}
            min={toDateInput(new Date(Date.now() + 86_400_000).toISOString())}
            onChange={(e) => setValidUntil(e.target.value)}
            aria-required={privileged || undefined}
            aria-invalid={expiryMissing || undefined}
          />
        </Field>
        <label className="checkbox-row">
          <input type="checkbox" checked={privileged} disabled readOnly />{" "}
          Privileged assignment
        </label>
      </SlideOver>

      <SlideOver
        open={bgOpen}
        title="Break-glass access"
        onClose={resetBreakGlass}
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={resetBreakGlass}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn-danger"
              disabled={!canBreakGlass}
              onClick={submitBreakGlass}
            >
              {breakGlass.isPending ? "Granting…" : "Grant emergency access"}
            </button>
          </>
        }
      >
        <p className="slideover-lede">
          Emergency access is time-boxed (max 24h), auto-expires, and is
          recorded as a high-visibility audit event. Use only when normal
          approval paths are unavailable.
        </p>
        <Field label="Subject email" required>
          <input
            value={bgSubject}
            onChange={(e) => setBgSubject(e.target.value)}
            placeholder="user@company.com"
            aria-required="true"
          />
        </Field>
        <Field label="Role" required>
          <select
            value={bgRole}
            onChange={(e) => setBgRole(e.target.value)}
            aria-required="true"
          >
            <option value="">Select a role…</option>
            {(roles.data ?? []).map((r) => (
              <option key={r.roleKey} value={r.roleKey}>
                {r.name}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Duration (hours)" required hint="Between 1 and 24 hours.">
          <input
            type="number"
            min={1}
            max={24}
            value={bgHours}
            onChange={(e) =>
              setBgHours(
                Math.max(1, Math.min(24, Number(e.target.value) || 1)),
              )
            }
            aria-required="true"
          />
        </Field>
        <Field label="Justification" required hint="Explains why emergency access is needed.">
          <textarea
            rows={3}
            value={bgReason}
            onChange={(e) => setBgReason(e.target.value)}
            placeholder="Incident reference and reason…"
            aria-required="true"
          />
        </Field>
      </SlideOver>

      <SlideOver
        open={!!editing}
        title="Edit assignment"
        onClose={() => setEditing(null)}
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setEditing(null)}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              disabled={!canSaveEdit}
              onClick={saveEdit}
            >
              {updateAssignment.isPending ? "Saving…" : "Save changes"}
            </button>
          </>
        }
      >
        {editing && (
          <>
            <div className="assignment-summary-card">
              <div>
                <span className="muted">Subject</span>
                <strong>{editing.subjectEmail}</strong>
              </div>
            </div>
            <p className="form-note">
              Subject is immutable — grant a new assignment to change who has
              access. Role, expiry and metadata can be edited here.
            </p>
            <Field label="Role" required>
              <select
                value={editRole}
                onChange={(e) => setEditRole(e.target.value)}
                aria-required="true"
              >
                {(roles.data ?? []).map((r) => (
                  <option key={r.roleKey} value={r.roleKey}>
                    {r.name}
                  </option>
                ))}
              </select>
            </Field>
            <Field
              label="Expires on"
              required={!!editRoleObj?.privileged}
              hint={
                editRoleObj?.privileged
                  ? "Required — privileged access must be time-boxed."
                  : "Leave empty for permanent access."
              }
              error={
                editExpiryMissing
                  ? "This role is privileged — an expiry date is required."
                  : undefined
              }
            >
              <input
                type="date"
                value={editUntil}
                min={toDateInput(
                  new Date(Date.now() + 86_400_000).toISOString(),
                )}
                onChange={(e) => setEditUntil(e.target.value)}
                aria-required={!!editRoleObj?.privileged || undefined}
                aria-invalid={editExpiryMissing || undefined}
              />
            </Field>
            <Field
              label="Reason"
              hint="Why this access is granted (recorded in the audit trail)."
            >
              <input
                value={editReason}
                onChange={(e) => setEditReason(e.target.value)}
                placeholder="e.g. Quarterly finance close"
              />
            </Field>
          </>
        )}
      </SlideOver>

      <SlideOver
        open={importOpen}
        title="Import assignments"
        onClose={resetImport}
        wide
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={resetImport}
            >
              {importResult && !importResult.dryRun ? "Close" : "Cancel"}
            </button>
            {importResult && importResult.dryRun && (
              <button
                type="button"
                className="btn-primary"
                disabled={
                  importAssignments.isPending || importResult.valid === 0
                }
                onClick={applyImport}
              >
                {importAssignments.isPending
                  ? "Applying…"
                  : importResult.valid === 0
                    ? "Nothing to apply"
                    : `Apply ${importResult.valid} change${importResult.valid === 1 ? "" : "s"}`}
              </button>
            )}
          </>
        }
      >
        <div className="import-panel">
          <p className="import-lede">
            Upload a CSV with columns{" "}
            <code>subjectEmail,roleKey,validUntil</code> (validUntil optional).
            Every row is validated and previewed before anything is applied.
            Duplicate grants are skipped and a later expiry updates the existing
            grant instead of creating another.
          </p>

          <label className="import-dropzone">
            <input
              type="file"
              accept=".csv,text/csv"
              aria-label="Assignments CSV file"
              onChange={(e) => onImportFile(e.target.files?.[0])}
            />
            <AppIcon name="upload" size={20} />
            <span className="import-dropzone-text">
              {importFileName ? (
                <>
                  <strong>{importFileName}</strong>
                  <span className="muted">Choose a different file…</span>
                </>
              ) : (
                <>
                  <strong>Choose a CSV file</strong>
                  <span className="muted">or drag and drop it here</span>
                </>
              )}
            </span>
          </label>

          {importAssignments.isPending && !importResult && (
            <Spinner label="Validating…" />
          )}

          {importResult && (
            <>
              <div className="import-summary" aria-live="polite">
                <span className="import-stat import-stat-create">
                  {importResult.created} to create
                </span>
                <span className="import-stat import-stat-update">
                  {importResult.updated} to update
                </span>
                <span className="import-stat import-stat-skip">
                  {importResult.skipped} skipped
                </span>
                {importResult.failed > 0 && (
                  <span className="import-stat import-stat-error">
                    {importResult.failed} error
                    {importResult.failed === 1 ? "" : "s"}
                  </span>
                )}
                {!importResult.dryRun && (
                  <span className="import-stat import-stat-applied">
                    {importResult.applied} applied
                  </span>
                )}
              </div>
              {importResult.dryRun && importResult.valid === 0 && (
                <p className="import-note muted">
                  {importResult.failed > 0
                    ? "Fix the errors below, then re-upload the file to try again."
                    : "Every row is a duplicate — there is nothing new to apply."}
                </p>
              )}
              <ul className="import-results">
                {importResult.results.map((r) => (
                  <li
                    key={r.row}
                    className={`import-row import-row-${r.status.toLowerCase()}`}
                  >
                    <span className="import-row-num">#{r.row}</span>
                    <span className="import-row-body">
                      <strong>{r.subjectEmail || "—"}</strong>
                      <span className="import-row-role">
                        {r.roleKey || "—"}
                      </span>
                      {r.message && (
                        <span className="import-row-msg muted">
                          {r.message}
                        </span>
                      )}
                    </span>
                    <span
                      className={`import-action import-action-${r.status.toLowerCase()}`}
                    >
                      {IMPORT_ACTION_LABELS[r.status]}
                    </span>
                  </li>
                ))}
              </ul>
            </>
          )}
        </div>
      </SlideOver>

      <VizModal
        title="Assignment graph"
        subtitle="Subject ↔ Role grants"
        open={graphOpen}
        onClose={() => setGraphOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-app" /> Subject
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-role" /> Role
            </span>
          </>
        }
      >
        <D3BipartiteGraph
          left={graphData.left}
          right={graphData.right}
          links={graphData.links}
          leftLabel="Subjects"
          rightLabel="Roles"
          height={640}
          ariaLabel="Subject and role assignment graph"
        />
      </VizModal>
    </>
  );
}
