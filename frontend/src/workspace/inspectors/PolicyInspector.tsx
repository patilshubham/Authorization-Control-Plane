import { useMemo, useState } from "react";
import type { PolicySummary, RoleSummary } from "../../types";
import {
  useDeletePolicy,
  useImpactAnalysis,
  usePublishPolicy,
  usePolicyHistory,
  useReferenceData,
  useUpdatePolicy,
} from "../../api/hooks";
import { useAiFeature } from "../../api/aiConfig";
import { userFacingError, type ImpactAnalysis } from "../../apiClient";
import { Field, Segmented, Spinner, StateBadge } from "../../components/primitives";
import { AppIcon } from "../../components/icons";
import { useToast } from "../../components/Toast";
import { ConfirmDialog } from "../../ui";
import { ConditionBuilder } from "../conditions/ConditionBuilder";
import { ObligationsEditor } from "../ObligationsEditor";
import { deserialize, summarize } from "../conditions/model";
import type { Selection } from "../selection";
import { useCapabilities } from "../../capabilities";
import { formatJson } from "../formatters";
import { describeEvent, relativeTime, actorLabel } from "../activity";
import { VizModal } from "../../components/viz/VizModal";
import { D3Tree } from "../../components/viz/D3Tree";
import { buildPolicyImpactTree } from "../../components/viz/graphModel";
import { nodeSelection } from "../../components/viz/nodeSelection";

// ── Policy inspector ────────────────────────────────────────────────────────────

export function PolicyInspector({
  appId,
  policy,
  roles,
  onSelect,
}: {
  appId: string;
  policy: PolicySummary;
  roles: RoleSummary[];
  onSelect: (s: Selection) => void;
}) {
  const publish = usePublishPolicy(appId);
  const update = useUpdatePolicy(appId);
  const remove = useDeletePolicy(appId);
  const history = usePolicyHistory(appId, policy.policyKey);
  const referenceData = useReferenceData(appId);
  const canManagePolicies = useCapabilities().can("ManagePolicies", appId);
  const referenceDataKeys = (referenceData.data ?? []).map((r) => r.key);
  const affectedRoles = roles.filter((r) =>
    r.permissions.includes(policy.permissionKey),
  );
  const conditionsJson =
    typeof policy.conditions === "string"
      ? policy.conditions
      : JSON.stringify(policy.conditions ?? {});
  // Only a DRAFT policy is editable and can be analyzed pre-publish; a PUBLISHED policy
  // is read-only here (edit by creating a new draft).
  const isDraft = policy.state === "DRAFT";
  const [editing, setEditing] = useState(false);
  const [showJson, setShowJson] = useState(false);
  const [draft, setDraft] = useState(conditionsJson);
  const [draftValid, setDraftValid] = useState(true);
  const [effect, setEffect] = useState<"ALLOW" | "DENY">(
    policy.effect as "ALLOW" | "DENY",
  );
  const [priority, setPriority] = useState(policy.priority ?? 0);
  const [obligations, setObligations] = useState(policy.obligations ?? "[]");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [graphOpen, setGraphOpen] = useState(false);
  const readableSummary = useMemo(
    () => summarize(deserialize(conditionsJson).root),
    [conditionsJson],
  );
  const obligationChips = useMemo(
    () => describeObligations(policy.obligations),
    [policy.obligations],
  );

  const saveConditions = () =>
    update.mutate(
      {
        policyKey: policy.policyKey,
        effect,
        conditions: draft,
        priority,
        obligations,
      },
      { onSuccess: () => setEditing(false) },
    );

  const removePolicy = () =>
    remove.mutate(policy.policyKey, {
      onSuccess: () => {
        setConfirmDelete(false);
        onSelect({ kind: "dashboard" });
      },
    });

  return (
    <article className="inspector">
      <header className="inspector-head">
        <div className="inspector-title-row">
          <span
            className={`effect-pip effect-${policy.effect.toLowerCase()}`}
            aria-hidden="true"
          />
          <h1>{policy.policyKey}</h1>
          <StateBadge value={policy.state} />
        </div>
        <p className="inspector-key">
          {policy.effect} on {policy.permissionKey}
        </p>
        <div className="inspector-action-bar">
          <button
            type="button"
            className="btn-secondary btn-sm"
            onClick={() => setGraphOpen(true)}
          >
            View impact
          </button>
          {canManagePolicies && (
            <button
              type="button"
              className="btn-danger btn-sm"
              onClick={() => setConfirmDelete(true)}
            >
              Delete policy
            </button>
          )}
        </div>
      </header>

      <ConfirmDialog
        open={confirmDelete}
        danger
        title={`Delete policy ${policy.policyKey}?`}
        message="This permanently removes the policy. Published policies stop being evaluated immediately."
        confirmLabel={remove.isPending ? "Deleting…" : "Delete policy"}
        onConfirm={removePolicy}
        onCancel={() => setConfirmDelete(false)}
      />

      {isDraft && canManagePolicies && (
        <div className="inspector-action-bar">
          <button
            type="button"
            className="btn-primary"
            disabled={publish.isPending}
            onClick={() => publish.mutate(policy.policyKey)}
          >
            Publish policy
          </button>
          <span className="muted">
            Draft policies are not evaluated until published.
          </span>
        </div>
      )}

      {isDraft && canManagePolicies && (
        <AiImpactAnalysis appId={appId} policyKey={policy.policyKey} />
      )}

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Conditions</h2>
          <div className="inspector-block-tools">
            <button
              type="button"
              className="mini-btn ghost"
              aria-pressed={showJson}
              onClick={() => setShowJson((s) => !s)}
            >
              {showJson ? "Hide JSON" : "View JSON"}
            </button>
            {isDraft && !editing && canManagePolicies && (
              <button
                type="button"
                className="mini-btn"
                onClick={() => {
                  setDraft(conditionsJson);
                  setEffect(policy.effect as "ALLOW" | "DENY");
                  setPriority(policy.priority ?? 0);
                  setObligations(policy.obligations ?? "[]");
                  setEditing(true);
                }}
              >
                Edit
              </button>
            )}
          </div>
        </div>

        {editing ? (
          <div className="policy-edit">
            <Field
              label="Effect"
              hint="ALLOW grants access when conditions match; DENY blocks it."
            >
              <Segmented
                ariaLabel="Policy effect"
                value={effect}
                onChange={(v) => setEffect(v as "ALLOW" | "DENY")}
                options={[
                  { value: "ALLOW", label: "Allow" },
                  { value: "DENY", label: "Deny" },
                ]}
              />
            </Field>
            <ConditionBuilder
              value={draft}
              onChange={setDraft}
              onValidityChange={setDraftValid}
              applicationId={appId}
              referenceDataKeys={referenceDataKeys}
              onAiEffectSuggested={setEffect}
            />
            <Field
              label="Priority"
              hint="Higher priority wins ties when several policies match."
            >
              <input
                type="number"
                value={priority}
                onChange={(e) => setPriority(Number(e.target.value) || 0)}
              />
            </Field>
            <Field
              label="Obligations"
              hint="Advisory instructions returned to the app when this policy decides."
            >
              <ObligationsEditor value={obligations} onChange={setObligations} />
            </Field>
            <div className="policy-edit-actions">
              <button
                type="button"
                className="btn-secondary"
                onClick={() => setEditing(false)}
              >
                Cancel
              </button>
              <button
                type="button"
                className="btn-primary"
                disabled={update.isPending || !draftValid}
                onClick={saveConditions}
              >
                {update.isPending ? "Saving…" : "Save conditions"}
              </button>
            </div>
          </div>
        ) : (
          <>
            <p className="policy-summary">{readableSummary}</p>
            {showJson && (
              <pre className="code-block">{formatJson(conditionsJson)}</pre>
            )}
          </>
        )}
      </section>

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Decision metadata</h2>
        </div>
        <dl className="detail-grid">
          <div>
            <dt>Priority</dt>
            <dd>{policy.priority ?? 0}</dd>
          </div>
          <div>
            <dt>Obligations</dt>
            <dd>
              {obligationChips.length === 0 ? (
                <span className="muted">None</span>
              ) : (
                <div className="chip-row">
                  {obligationChips.map((o) => (
                    <span key={o} className="chip">
                      {o}
                    </span>
                  ))}
                </div>
              )}
            </dd>
          </div>
        </dl>
      </section>

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Affects roles granting {policy.permissionKey}</h2>
          <span className="muted">{affectedRoles.length}</span>
        </div>
        {affectedRoles.length === 0 ? (
          <p className="muted">
            No role currently grants the governed permission.
          </p>
        ) : (
          <div className="chip-row">
            {affectedRoles.map((r) => (
              <button
                key={r.roleKey}
                type="button"
                className="chip chip-granted"
                onClick={() => onSelect({ kind: "role", key: r.roleKey })}
              >
                {r.name}
              </button>
            ))}
          </div>
        )}
      </section>

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>History</h2>
          {history.data && history.data.entries.length > 0 && (
            <span className="muted">
              {history.data.entries.length} change
              {history.data.entries.length === 1 ? "" : "s"}
            </span>
          )}
        </div>
        {history.isLoading ? (
          <Spinner label="Loading history…" />
        ) : !history.data || history.data.entries.length === 0 ? (
          <p className="muted">No recorded changes yet.</p>
        ) : (
          <ol className="policy-history">
            {history.data.entries.map((entry, i) => {
              const meta = describeEvent(entry.eventType);
              return (
                <li key={i} className="policy-history-item">
                  <span
                    className={`activity-icon tone-${meta.tone}`}
                    aria-hidden="true"
                  >
                    {meta.icon}
                  </span>
                  <div className="policy-history-body">
                    <div className="policy-history-head">
                      <strong>{meta.label}</strong>
                      <span className="muted">
                        {actorLabel(entry.actor, entry.actorRole)}
                      </span>
                      <span
                        className="muted"
                        title={new Date(entry.timestamp).toLocaleString()}
                      >
                        {relativeTime(entry.timestamp)}
                      </span>
                    </div>
                    {(entry.oldValue || entry.newValue) && (
                      <div className="policy-history-diff">
                        {entry.oldValue && (
                          <div>
                            <span className="activity-detail-label">Before</span>
                            <pre className="code-block">
                              {formatJson(entry.oldValue)}
                            </pre>
                          </div>
                        )}
                        {entry.newValue && (
                          <div>
                            <span className="activity-detail-label">After</span>
                            <pre className="code-block">
                              {formatJson(entry.newValue)}
                            </pre>
                          </div>
                        )}
                      </div>
                    )}
                  </div>
                </li>
              );
            })}
          </ol>
        )}
      </section>

      <VizModal
        title={`${policy.policyKey} — impact`}
        subtitle="Policy → Permission → Roles"
        open={graphOpen}
        onClose={() => setGraphOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-policy" /> Policy
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-perm" /> Permission
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-role" /> Role
            </span>
          </>
        }
      >
        <D3Tree
          data={buildPolicyImpactTree(policy, undefined, affectedRoles)}
          height={640}
          onSelect={(node) => {
            const sel = nodeSelection(node);
            if (sel) {
              onSelect(sel);
              setGraphOpen(false);
            }
          }}
          ariaLabel={`${policy.policyKey} impact`}
        />
      </VizModal>
    </article>
  );
}

// Render a policy's stored obligations JSON as human-readable chip labels.
// Accepts bare id strings or { id, value } objects; malformed input yields none.
function describeObligations(json: string | undefined): string[] {
  if (!json || !json.trim()) return [];
  try {
    const parsed = JSON.parse(json);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map((entry): string | null => {
        if (typeof entry === "string") return entry;
        if (entry && typeof entry === "object" && typeof entry.id === "string") {
          return entry.value == null || entry.value === ""
            ? entry.id
            : `${entry.id} = ${entry.value}`;
        }
        return null;
      })
      .filter((label): label is string => label !== null);
  } catch {
    return [];
  }
}

/**
 * F5 — Pre-publish impact analysis. Shadow-evaluates the draft policy against a representative set
 * of requests and reports, in plain language, exactly which subjects flip between allow and deny.
 * Deterministic diff, AI narration. Advisory only; only mounted when the feature is on.
 */
function AiImpactAnalysis({
  appId,
  policyKey,
}: {
  appId: string;
  policyKey: string;
}) {
  const enabled = useAiFeature("impactAnalysis");
  const analyze = useImpactAnalysis();
  const toast = useToast();
  const [result, setResult] = useState<ImpactAnalysis | null>(null);

  if (!enabled) return null;

  async function run() {
    // Clear the previous analysis up front so stale results never linger if the
    // new request fails.
    setResult(null);
    try {
      const impact = await analyze.mutateAsync({
        applicationId: appId,
        policyKey,
      });
      setResult(impact);
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  const changeCount = result
    ? result.allowToDenyCount + result.denyToAllowCount
    : 0;

  return (
    <div className="sim-explain">
      <button
        type="button"
        className="mini-btn is-active"
        disabled={analyze.isPending}
        onClick={run}
      >
        <AppIcon name="sparkles" size={14} />{" "}
        {analyze.isPending ? "Analyzing…" : "Analyze publish impact"}
      </button>
      {result && (
        <div className="sim-explain-body" aria-live="polite">
          <p className="sim-explain-narrative">{result.summary}</p>
          <div className="impact-metrics">
            <span className="impact-metric">
              <strong>{result.evaluatedCount}</strong> evaluated
            </span>
            <span className="impact-metric impact-metric-deny">
              <strong>{result.allowToDenyCount}</strong> newly denied
            </span>
            <span className="impact-metric impact-metric-allow">
              <strong>{result.denyToAllowCount}</strong> newly allowed
            </span>
          </div>
          {changeCount > 0 && result.flips.length > 0 && (
            <div className="impact-table-scroll">
              <table className="impact-table">
                <thead>
                  <tr>
                    <th>Subject</th>
                    <th>Resource</th>
                    <th>Change</th>
                  </tr>
                </thead>
                <tbody>
                  {result.flips.map((flip, index) => (
                    <tr key={index}>
                      <td>{flip.subjectEmail ?? "—"}</td>
                      <td>{flip.resourceId ?? "—"}</td>
                      <td>
                        <span
                          className={`impact-flip impact-flip-${flip.after ? "allow" : "deny"}`}
                        >
                          {flip.before ? "Allow" : "Deny"} →{" "}
                          {flip.after ? "Allow" : "Deny"}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {!result.sampledFromHistory && (
            <p className="cond-ai-hint">
              No recorded decisions were available, so this used current
              assignments with empty context — treat it as a lower bound.
            </p>
          )}
          <p className="cond-ai-hint">
            AI-generated impact estimate — advisory only. Publishing is never
            blocked by this analysis.
          </p>
        </div>
      )}
    </div>
  );
}
