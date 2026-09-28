import { useMemo, useState } from "react";
import {
  useActivateReviewCampaign,
  useCreateReviewCampaign,
  useDecideReviewItem,
  useDecideReviewItemsBulk,
  useFinalizeReviewCampaign,
  useReviewCampaign,
  useReviewCampaigns,
} from "../api/hooks";
import { useCapabilities } from "../capabilities";
import { EmptyBlock, Field, Segmented, Spinner } from "../components/primitives";
import { ConfirmDialog, StatusChip } from "../ui";
import { userFacingError, type ReviewItem } from "../apiClient";
import { useToast } from "../components/Toast";
import { relativeTime } from "./activity";

// P14 — Scheduled certification (recertification) campaigns. Create a campaign,
// activate it to snapshot the app's active assignments into a reviewer worklist,
// record KEEP / REVOKE / NEEDS_INFO (with an optional note) per item, then finalize
// to apply the approved revocations through the normal audited path.

const DECISIONS = ["KEEP", "REVOKE", "NEEDS_INFO"] as const;
type Decision = (typeof DECISIONS)[number];

const DECISION_META: Record<
  Decision,
  { label: string; verb: string; tone: string }
> = {
  KEEP: { label: "Keep", verb: "Kept", tone: "keep" },
  REVOKE: { label: "Revoke", verb: "Revoked", tone: "revoke" },
  NEEDS_INFO: { label: "Needs info", verb: "Flagged", tone: "needs_info" },
};

type FilterValue = "ALL" | "PENDING" | Decision;

export function Certifications({ appId }: { appId: string }) {
  const caps = useCapabilities();
  const canManage = caps.can("AssignRoles", appId);
  const campaigns = useReviewCampaigns(appId);
  const create = useCreateReviewCampaign(appId);
  const activate = useActivateReviewCampaign(appId);
  const decide = useDecideReviewItem(appId);
  const decideBulk = useDecideReviewItemsBulk(appId);
  const finalize = useFinalizeReviewCampaign(appId);
  const toast = useToast();

  const [selected, setSelected] = useState<string | null>(null);
  const detail = useReviewCampaign(appId, selected);
  const [name, setName] = useState("");
  const [confirmFinalize, setConfirmFinalize] = useState(false);
  const [filter, setFilter] = useState<FilterValue>("ALL");
  // Local note drafts keyed by item id, so a reviewer can type before committing a decision.
  const [noteDrafts, setNoteDrafts] = useState<Record<string, string>>({});

  const list = campaigns.data ?? [];
  const current = detail.data;
  const items = current?.items ?? [];

  const counts = useMemo(() => {
    const c: Record<string, number> = {
      PENDING: 0,
      KEEP: 0,
      REVOKE: 0,
      NEEDS_INFO: 0,
    };
    for (const i of items) c[i.decision] = (c[i.decision] ?? 0) + 1;
    return c;
  }, [items]);

  const filteredItems = useMemo(
    () =>
      filter === "ALL" ? items : items.filter((i) => i.decision === filter),
    [items, filter],
  );

  const revokeCount = counts.REVOKE ?? 0;
  const pendingCount = counts.PENDING ?? 0;
  const reviewed = items.length - pendingCount;

  async function createCampaign() {
    const trimmed = name.trim();
    if (!trimmed) return;
    try {
      const created = await create.mutateAsync({ name: trimmed });
      setName("");
      setSelected(created.id);
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  function noteFor(item: ReviewItem): string {
    return noteDrafts[item.id] ?? item.decisionNote ?? "";
  }

  function setDecision(item: ReviewItem, decision: Decision) {
    if (!selected) return;
    const note = noteFor(item).trim();
    decide.mutate(
      {
        campaignId: selected,
        itemId: item.id,
        decision,
        note: note || undefined,
      },
      {
        onSuccess: () =>
          toast.success(
            `${DECISION_META[decision].verb} ${item.subjectEmail}.`,
          ),
        onError: (e) => toast.error(userFacingError(e)),
      },
    );
  }

  function resetDecision(item: ReviewItem) {
    if (!selected) return;
    decide.mutate(
      { campaignId: selected, itemId: item.id, decision: "PENDING" },
      {
        onSuccess: () =>
          toast.success(`Reset ${item.subjectEmail} to pending.`),
        onError: (e) => toast.error(userFacingError(e)),
      },
    );
  }

  // Persist an edited note for an already-decided item, keeping its decision.
  function persistNote(item: ReviewItem) {
    if (!selected) return;
    const draft = noteDrafts[item.id];
    if (draft === undefined) return; // untouched
    const next = draft.trim();
    if (next === (item.decisionNote ?? "")) return; // no change
    if (item.decision === "PENDING") return; // saved together with the decision instead
    decide.mutate({
      campaignId: selected,
      itemId: item.id,
      decision: item.decision,
      note: next || undefined,
    });
  }

  function keepAllRemaining() {
    if (!selected) return;
    const n = pendingCount;
    decideBulk.mutate(
      { campaignId: selected, decision: "KEEP" },
      {
        onSuccess: () =>
          toast.success(`Kept ${n} remaining item${n === 1 ? "" : "s"}.`),
        onError: (e) => toast.error(userFacingError(e)),
      },
    );
  }

  // ── Campaign detail ────────────────────────────────────────────────────────
  if (selected && current) {
    const c = current.campaign;
    const editable = c.status === "ACTIVE" && canManage;
    const filterOptions: Array<{ value: FilterValue; label: string }> = [
      { value: "ALL", label: `All (${items.length})` },
      { value: "PENDING", label: `Pending (${pendingCount})` },
      { value: "KEEP", label: `Keep (${counts.KEEP ?? 0})` },
      { value: "REVOKE", label: `Revoke (${revokeCount})` },
      { value: "NEEDS_INFO", label: `Needs info (${counts.NEEDS_INFO ?? 0})` },
    ];
    return (
      <>
        <header className="page-head">
          <div className="page-head-titles">
            <nav className="breadcrumbs" aria-label="Breadcrumb">
              <button
                type="button"
                className="link-chip"
                onClick={() => {
                  setSelected(null);
                  setFilter("ALL");
                  setNoteDrafts({});
                }}
              >
                Certifications
              </button>
              <span aria-hidden="true">/</span>
              <span>{c.name}</span>
            </nav>
            <h1>
              {c.name} <StatusChip value={c.status} />
            </h1>
            <p className="page-sub">
              {c.itemCount} item{c.itemCount === 1 ? "" : "s"} · {pendingCount}{" "}
              pending · {counts.KEEP ?? 0} keep · {revokeCount} revoke ·{" "}
              {counts.NEEDS_INFO ?? 0} needs info
            </p>
          </div>
          <div className="page-head-actions">
            {c.status === "DRAFT" && canManage && (
              <button
                type="button"
                className="btn-primary"
                disabled={activate.isPending}
                onClick={() =>
                  activate.mutate(c.id, {
                    onError: (e) => toast.error(userFacingError(e)),
                  })
                }
              >
                {activate.isPending ? "Activating…" : "Activate campaign"}
              </button>
            )}
            {editable && pendingCount > 0 && (
              <button
                type="button"
                className="btn-secondary"
                disabled={decideBulk.isPending}
                onClick={keepAllRemaining}
              >
                {decideBulk.isPending
                  ? "Applying…"
                  : `Keep all remaining (${pendingCount})`}
              </button>
            )}
            {c.status === "ACTIVE" && canManage && (
              <button
                type="button"
                className="btn-primary"
                disabled={finalize.isPending}
                onClick={() => setConfirmFinalize(true)}
              >
                Finalize
              </button>
            )}
          </div>
        </header>

        {c.status === "ACTIVE" && items.length > 0 && (
          <div className="cert-progress">
            <div
              className="cert-progress-track"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={items.length}
              aria-valuenow={reviewed}
              aria-label="Review progress"
            >
              <div
                className="cert-progress-fill"
                style={{
                  width: `${items.length ? Math.round((reviewed / items.length) * 100) : 0}%`,
                }}
              />
            </div>
            <span className="muted">
              {reviewed} of {items.length} reviewed
            </span>
          </div>
        )}

        {items.length === 0 ? (
          <EmptyBlock
            title="No items"
            hint={
              c.status === "DRAFT"
                ? "Activate the campaign to snapshot active assignments into review items."
                : "No active assignments were in scope when this campaign was activated."
            }
          />
        ) : (
          <>
            <div className="cert-filters">
              <Segmented
                ariaLabel="Filter by decision"
                value={filter}
                onChange={(v) => setFilter(v as FilterValue)}
                options={filterOptions}
              />
            </div>
            {filteredItems.length === 0 ? (
              <EmptyBlock
                title="Nothing here"
                hint="No items match this filter."
              />
            ) : (
              <ul className="cert-items">
                {filteredItems.map((item) => {
                  const decided = item.decision !== "PENDING";
                  return (
                    <li key={item.id} className="cert-item">
                      <div className="cert-item-head">
                        <span className="cert-item-body">
                          <strong>{item.subjectEmail}</strong>
                          <span className="sod-scope-tag">{item.roleKey}</span>
                        </span>
                        <StatusChip value={item.decision} />
                      </div>
                      {editable && (
                        <div className="cert-item-decisions">
                          {DECISIONS.map((d) => (
                            <button
                              key={d}
                              type="button"
                              className={`mini-btn cert-btn-${DECISION_META[d].tone}${item.decision === d ? " is-active" : ""}`}
                              disabled={decide.isPending}
                              aria-pressed={item.decision === d}
                              onClick={() => setDecision(item, d)}
                            >
                              {DECISION_META[d].label}
                            </button>
                          ))}
                          {decided && (
                            <button
                              type="button"
                              className="mini-btn ghost"
                              disabled={decide.isPending}
                              onClick={() => resetDecision(item)}
                            >
                              Undo
                            </button>
                          )}
                        </div>
                      )}
                      {editable ? (
                        <textarea
                          className="cert-item-note"
                          rows={1}
                          placeholder="Add a note (optional)…"
                          value={noteFor(item)}
                          onChange={(e) =>
                            setNoteDrafts((prev) => ({
                              ...prev,
                              [item.id]: e.target.value,
                            }))
                          }
                          onBlur={() => persistNote(item)}
                        />
                      ) : (
                        item.decisionNote && (
                          <p className="cert-item-note-static muted">
                            “{item.decisionNote}”
                          </p>
                        )
                      )}
                      {decided && item.decidedBy && (
                        <p className="cert-item-decided muted">
                          {DECISION_META[item.decision as Decision]?.verb ??
                            "Decided"}{" "}
                          by {item.decidedBy}
                          {item.decidedAt
                            ? ` · ${relativeTime(item.decidedAt)}`
                            : ""}
                        </p>
                      )}
                    </li>
                  );
                })}
              </ul>
            )}
          </>
        )}

        <ConfirmDialog
          open={confirmFinalize}
          danger
          title="Finalize campaign?"
          message={
            `This closes the campaign and revokes the ${revokeCount} assignment${revokeCount === 1 ? "" : "s"} marked REVOKE (audited).` +
            (pendingCount > 0
              ? ` ${pendingCount} still-pending item${pendingCount === 1 ? "" : "s"} will be left untouched (access kept).`
              : "") +
            " This cannot be undone."
          }
          confirmLabel={finalize.isPending ? "Finalizing…" : "Finalize & apply"}
          confirmDisabled={finalize.isPending}
          onConfirm={() =>
            finalize.mutate(c.id, {
              onSuccess: () => setConfirmFinalize(false),
              onError: (e) => toast.error(userFacingError(e)),
            })
          }
          onCancel={() => setConfirmFinalize(false)}
        />
      </>
    );
  }

  // ── Campaign list ──────────────────────────────────────────────────────────
  return (
    <>
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Certifications</h1>
          <p className="page-sub">
            Periodic access reviews. Activate a campaign to generate a reviewer
            worklist, then finalize to apply approved revocations.
          </p>
        </div>
      </header>

      {canManage && (
        <div className="panel cert-create">
          <Field label="New campaign" required>
            <input
              value={name}
              placeholder="e.g. Q3 access review"
              aria-required="true"
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") createCampaign();
              }}
            />
          </Field>
          <button
            type="button"
            className="btn-primary"
            disabled={!name.trim() || create.isPending}
            onClick={createCampaign}
          >
            {create.isPending ? "Creating…" : "Create"}
          </button>
        </div>
      )}

      {campaigns.isLoading ? (
        <Spinner label="Loading campaigns…" />
      ) : list.length === 0 ? (
        <EmptyBlock
          title="No campaigns yet"
          hint="Create a campaign to start a periodic access review."
        />
      ) : (
        <ul className="cert-list">
          {list.map((c) => (
            <li key={c.id}>
              <button
                type="button"
                className="cert-card"
                onClick={() => setSelected(c.id)}
              >
                <span className="cert-card-body">
                  <strong>{c.name}</strong>
                  <span className="muted">
                    {c.itemCount} item{c.itemCount === 1 ? "" : "s"}
                    {c.status === "ACTIVE" && c.pendingCount > 0
                      ? ` · ${c.pendingCount} pending`
                      : ""}
                  </span>
                </span>
                <StatusChip value={c.status} />
              </button>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
