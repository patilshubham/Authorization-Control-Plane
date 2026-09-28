import { useMemo, useState } from "react";
import {
  useAssignments,
  useRevokeAssignment,
  useUpdateAssignmentExpiry,
} from "../api/hooks";
import type { AssignmentSummary } from "../types";
import { useCapabilities } from "../capabilities";
import { AppIcon } from "../components/icons";
import { Field, Segmented, Spinner } from "../components/primitives";
import { ConfirmDialog, DrawerPanel } from "../ui";
import { userFacingError } from "../apiClient";
import { useToast } from "../components/Toast";
import type { Selection } from "./selection";

// P1 — Expiring access. A proactive, deterministic view of time-bounded grants
// about to lapse, sourced from the assignments the dashboard already loads (no
// extra fetch). Extend/revoke reuse the existing audited assignment endpoints;
// the window toggle (7/30 days) is a pure client-side filter.

const DAY_MS = 86_400_000;

/** Whole days from now until an ISO timestamp (rounded up; negative when past). */
function daysUntil(iso: string): number {
  return Math.ceil((new Date(iso).getTime() - Date.now()) / DAY_MS);
}

/** yyyy-mm-dd value an <input type="date"> expects. */
function toDateInput(iso: string | null): string {
  return iso ? new Date(iso).toISOString().slice(0, 10) : "";
}

/** Convert a date-input value (yyyy-mm-dd) to an end-of-day ISO string for the API. */
function fromDateInput(value: string): string {
  return new Date(`${value}T23:59:59`).toISOString();
}

function remainingLabel(days: number): string {
  if (days <= 0) return "Expires today";
  if (days === 1) return "Expires tomorrow";
  return `Expires in ${days} days`;
}

export function ExpiringAccess({
  appId,
  onSelect,
}: {
  appId: string;
  onSelect: (s: Selection) => void;
}) {
  const caps = useCapabilities();
  const canView = caps.can("ReadOnlyView", appId);
  const canAssign = caps.can("AssignRoles", appId);
  const assignments = useAssignments(appId, canView);
  const revoke = useRevokeAssignment(appId);
  const updateExpiry = useUpdateAssignmentExpiry(appId);
  const toast = useToast();

  const [windowDays, setWindowDays] = useState<"7" | "30">("7");
  const [extending, setExtending] = useState<AssignmentSummary | null>(null);
  const [newDate, setNewDate] = useState("");
  const [pendingRevoke, setPendingRevoke] = useState<AssignmentSummary | null>(
    null,
  );

  const windowCount = Number(windowDays);
  const expiring = useMemo(() => {
    const list = assignments.data ?? [];
    return list
      .filter((a) => {
        if (!a.validUntil) return false;
        if ((a.state ?? "").toUpperCase() !== "ACTIVE") return false;
        const days = daysUntil(a.validUntil);
        return days >= 0 && days <= windowCount;
      })
      .sort(
        (a, b) =>
          new Date(a.validUntil!).getTime() - new Date(b.validUntil!).getTime(),
      );
  }, [assignments.data, windowCount]);

  if (!canView) return null;

  const today = new Date().toISOString().slice(0, 10);

  function openExtend(a: AssignmentSummary) {
    setExtending(a);
    setNewDate(toDateInput(a.validUntil));
  }

  async function confirmExtend() {
    if (!extending?.id || !newDate) return;
    try {
      await updateExpiry.mutateAsync({
        assignmentId: extending.id,
        validUntil: fromDateInput(newDate),
      });
      setExtending(null);
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  async function confirmRevoke() {
    if (!pendingRevoke?.id) return;
    try {
      await revoke.mutateAsync(pendingRevoke.id);
      setPendingRevoke(null);
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  return (
    <section
      className="dash-card dash-card-wide advisor-card"
      aria-label="Expiring access"
    >
      <div className="dash-card-head">
        <h2>
          <AppIcon name="active" size={16} /> Expiring access
        </h2>
        <div className="advisor-head-tools">
          {expiring.length > 0 && (
            <span className="muted">
              {expiring.length} expiring within {windowCount} days
            </span>
          )}
          <Segmented
            ariaLabel="Expiry window"
            value={windowDays}
            onChange={setWindowDays}
            options={[
              { value: "7", label: "7d" },
              { value: "30", label: "30d" },
            ]}
          />
        </div>
      </div>

      {assignments.isLoading ? (
        <Spinner label="Loading assignments…" />
      ) : assignments.isError ? (
        <p className="muted">Could not load assignments.</p>
      ) : expiring.length === 0 ? (
        <p className="advisor-empty">
          <AppIcon name="active" size={16} /> No active grants expire within the
          next {windowCount} days.
        </p>
      ) : (
        <ul className="advisor-list advisor-list-scroll">
          {expiring.map((a) => {
            const days = daysUntil(a.validUntil!);
            const tone = days <= 1 ? "danger" : days <= 7 ? "warning" : "muted";
            return (
              <li key={a.id ?? `${a.subjectEmail}-${a.roleKey}`} className="advisor-item">
                <span className="advisor-body">
                  <span className="advisor-title">
                    {a.subjectEmail}
                    <span className="sod-scope-tag">{a.roleKey}</span>
                  </span>
                  <span className={`expiry expiry-${tone}`}>
                    {remainingLabel(days)}
                  </span>
                </span>
                {canAssign && a.id && (
                  <span className="expiring-actions">
                    <button
                      type="button"
                      className="mini-btn ghost"
                      disabled={updateExpiry.isPending}
                      onClick={() => openExtend(a)}
                    >
                      Extend
                    </button>
                    <button
                      type="button"
                      className="mini-btn ghost"
                      disabled={revoke.isPending}
                      onClick={() => setPendingRevoke(a)}
                    >
                      Revoke
                    </button>
                  </span>
                )}
              </li>
            );
          })}
        </ul>
      )}

      {expiring.length > 0 && (
        <button
          type="button"
          className="mini-btn ghost advisor-open"
          onClick={() => onSelect({ kind: "access" })}
        >
          View all assignments
        </button>
      )}

      <DrawerPanel
        open={extending !== null}
        title="Extend access"
        onClose={() => setExtending(null)}
      >
        {extending && (
          <div className="drawer-body">
            <p className="muted">
              {extending.subjectEmail} · {extending.roleKey}
            </p>
            <Field label="New expiry date" hint="The grant stays active until the end of this day.">
              <input
                type="date"
                min={today}
                value={newDate}
                onChange={(e) => setNewDate(e.target.value)}
              />
            </Field>
            <div className="dialog-actions">
              <button
                type="button"
                className="ghost"
                onClick={() => setExtending(null)}
              >
                Cancel
              </button>
              <button
                type="button"
                className="btn-primary"
                disabled={!newDate || updateExpiry.isPending}
                onClick={confirmExtend}
              >
                {updateExpiry.isPending ? "Saving…" : "Save expiry"}
              </button>
            </div>
          </div>
        )}
      </DrawerPanel>

      <ConfirmDialog
        open={pendingRevoke !== null}
        title="Revoke access"
        message={
          pendingRevoke
            ? `Revoke ${pendingRevoke.roleKey} from ${pendingRevoke.subjectEmail}? They lose this access immediately.`
            : ""
        }
        confirmLabel="Revoke access"
        danger
        confirmDisabled={revoke.isPending}
        onConfirm={confirmRevoke}
        onCancel={() => setPendingRevoke(null)}
      />
    </section>
  );
}
