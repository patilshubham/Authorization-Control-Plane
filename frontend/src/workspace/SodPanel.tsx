import { useState } from "react";
import {
  useSodRules,
  useSodViolations,
  useDraftSodRule,
  useSaveSodRule,
  useDeleteSodRule,
} from "../api/hooks";
import { useAiFeature } from "../api/aiConfig";
import { useCapabilities } from "../capabilities";
import { AppIcon } from "../components/icons";
import { RiskDot, Spinner } from "../components/primitives";
import { ConfirmDialog, StatusChip } from "../ui";
import {
  userFacingError,
  type SodMatcher,
  type SodRule,
  type SodRuleDraft,
  type SodViolation,
} from "../apiClient";
import { useToast } from "../components/Toast";
import type { Selection } from "./selection";

// F7 — Separation of duties. The violations list is produced by a fully
// deterministic backend scan of the structured access model (roles, published
// grants and active assignments) — it renders regardless of the AI switch.
// The AI is used only to *draft* a rule's two permission matchers from plain
// language; every draft is grounded in the application's real vocabulary and
// validated server-side before it can be saved. AI never decides a violation.

function violationSelection(violation: SodViolation): Selection | null {
  if (!violation.deepLinkKey) return null;
  switch (violation.deepLinkKind) {
    case "role":
      return { kind: "role", key: violation.deepLinkKey };
    case "user":
      return { kind: "user", email: violation.deepLinkKey };
    default:
      return null;
  }
}

function describeMatcher(matcher: SodMatcher): string {
  if (matcher.permissionKey) return matcher.permissionKey;
  if (matcher.resource && matcher.action)
    return `${matcher.action} ${matcher.resource}`;
  return matcher.action ?? matcher.resource ?? "(unspecified)";
}

function slugify(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 60);
}

// A single finding row. Reused both nested under its rule and (defensively) in
// the ungrouped list. When shown under its rule the rule name is redundant, so
// the subject (the conflicted role/user) becomes the row title instead.
function SodViolationItem({
  violation,
  showRuleName,
  onSelect,
}: {
  violation: SodViolation;
  showRuleName: boolean;
  onSelect: (s: Selection) => void;
}) {
  const sel = violationSelection(violation);
  return (
    <li className="advisor-item">
      <span className="advisor-sev" title={`${violation.severity} severity`}>
        <RiskDot level={violation.severity} />
        <StatusChip value={violation.severity} />
      </span>
      <span className="advisor-body">
        <span className="advisor-title">
          {showRuleName ? violation.ruleName : violation.subjectLabel}
          <span className="sod-scope-tag">
            {violation.scope === "ROLE" ? "Role" : "User"}
          </span>
        </span>
        <span className="advisor-detail muted">{violation.detail}</span>
        {violation.conflictingPermissions.length > 0 && (
          <span className="sod-perm-chips">
            {violation.conflictingPermissions.map((perm) => (
              <span key={perm} className="sod-chip">
                {perm}
              </span>
            ))}
          </span>
        )}
      </span>
      {sel && (
        <button
          type="button"
          className="mini-btn ghost advisor-open"
          onClick={() => onSelect(sel)}
        >
          Open {violation.subjectLabel}
        </button>
      )}
    </li>
  );
}

export function SodPanel({
  appId,
  onSelect,
}: {
  appId: string;
  onSelect: (s: Selection) => void;
}) {
  const caps = useCapabilities();
  const canView = caps.can("ReadOnlyView", appId);
  const canManage = caps.can("ManagePolicies", appId);
  const aiEnabled = useAiFeature("sodAnalysis");
  // Rule authoring (draft/save/delete) flows exclusively through the AI-assisted
  // endpoints, so it requires AI on. The deterministic rules/violations below
  // render regardless of AI, purely on view capability.
  const canAuthor = aiEnabled && canManage;
  const violationsQuery = useSodViolations(appId, canView);
  const rulesQuery = useSodRules(appId, canView);
  const draftMutation = useDraftSodRule();
  const saveMutation = useSaveSodRule();
  const deleteMutation = useDeleteSodRule();
  const toast = useToast();

  const [composerOpen, setComposerOpen] = useState(false);
  const [instruction, setInstruction] = useState("");
  const [draft, setDraft] = useState<SodRuleDraft | null>(null);
  const [ruleKey, setRuleKey] = useState("");
  const [name, setName] = useState("");
  const [pendingDelete, setPendingDelete] = useState<SodRule | null>(null);
  // Which rules are expanded. `null` means "use the default" (rules that have
  // findings start open); once the user toggles anything it holds the explicit
  // open set.
  const [openRuleKeys, setOpenRuleKeys] = useState<Set<string> | null>(null);

  if (!canView) return null;

  const violations = violationsQuery.data?.violations ?? [];
  const rules = rulesQuery.data?.rules ?? [];
  const highCount = violations.filter(
    (v) => v.severity === "HIGH" || v.severity === "CRITICAL",
  ).length;

  // Group findings under the rule that produced them so each rule can be
  // expanded to reveal its own linked violations.
  const violationsByRule = new Map<string, SodViolation[]>();
  for (const v of violations) {
    const bucket = violationsByRule.get(v.ruleKey);
    if (bucket) bucket.push(v);
    else violationsByRule.set(v.ruleKey, [v]);
  }
  const configuredKeys = new Set(rules.map((r) => r.ruleKey));
  // Defensive: a finding whose rule isn't in the configured list (e.g. a
  // transient load mismatch) is still surfaced so nothing is silently hidden.
  const ungroupedViolations = violations.filter(
    (v) => !configuredKeys.has(v.ruleKey),
  );

  function isRuleOpen(ruleKey: string, hasFindings: boolean): boolean {
    return openRuleKeys === null ? hasFindings : openRuleKeys.has(ruleKey);
  }
  function toggleRule(ruleKey: string) {
    setOpenRuleKeys((prev) => {
      const base =
        prev ??
        new Set(
          rules
            .filter((r) => (violationsByRule.get(r.ruleKey)?.length ?? 0) > 0)
            .map((r) => r.ruleKey),
        );
      const next = new Set(base);
      if (next.has(ruleKey)) next.delete(ruleKey);
      else next.add(ruleKey);
      return next;
    });
  }

  async function generateDraft() {
    const text = instruction.trim();
    if (text.length === 0) return;
    // Clear any previous draft up front so stale matchers never linger if the
    // new instruction fails to produce a draft.
    setDraft(null);
    try {
      const result = await draftMutation.mutateAsync({ appId, instruction: text });
      setDraft(result);
      setName(result.name);
      setRuleKey(slugify(result.name));
      if (result.warnings.length > 0) {
        toast.info(result.warnings[0]);
      } else {
        toast.success("Draft ready. Review the matchers before saving.");
      }
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  async function saveDraft() {
    if (!draft) return;
    const key = ruleKey.trim();
    const ruleName = name.trim();
    if (key.length === 0 || ruleName.length === 0) {
      toast.error("Rule key and name are required.");
      return;
    }
    try {
      await saveMutation.mutateAsync({
        appId,
        rule: {
          ruleKey: key,
          name: ruleName,
          rationale: draft.rationale,
          severity: draft.severity,
          matcherA: draft.matcherA,
          matcherB: draft.matcherB,
        },
      });
      toast.success("Rule saved. Violations refreshed.");
      resetComposer();
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  function resetComposer() {
    setComposerOpen(false);
    setInstruction("");
    setDraft(null);
    setRuleKey("");
    setName("");
  }

  async function confirmDelete() {
    const rule = pendingDelete;
    if (!rule) return;
    try {
      await deleteMutation.mutateAsync({ appId, ruleKey: rule.ruleKey });
      toast.success("Rule deleted. Violations refreshed.");
      setPendingDelete(null);
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  const draftIncomplete =
    draft !== null &&
    (describeMatcher(draft.matcherA) === "(unspecified)" ||
      describeMatcher(draft.matcherB) === "(unspecified)");

  return (
    <section
      className="dash-card dash-card-wide advisor-card"
      aria-label="Separation of duties"
    >
      <div className="dash-card-head">
        <h2>
          <AppIcon name="sparkles" size={16} /> Separation of duties
        </h2>
        <div className="advisor-head-tools">
          {violations.length > 0 && (
            <span className="muted">
              {violations.length} violation{violations.length === 1 ? "" : "s"}
              {highCount > 0 ? ` · ${highCount} high` : ""}
            </span>
          )}
          {canAuthor && (
            <button
              type="button"
              className="mini-btn is-active"
              disabled={saveMutation.isPending}
              onClick={() =>
                composerOpen ? resetComposer() : setComposerOpen(true)
              }
            >
              <AppIcon name="sparkles" size={14} />{" "}
              {composerOpen ? "Close" : "Draft rule with AI"}
            </button>
          )}
        </div>
      </div>

      {composerOpen && canAuthor && (
        <div className="cond-ai" role="group" aria-label="Draft SoD rule with AI">
          <label className="cond-ai-label" htmlFor="sod-ai-instruction">
            <AppIcon name="sparkles" size={14} /> Describe the toxic combination
          </label>
          <div className="cond-ai-row">
            <textarea
              id="sod-ai-instruction"
              className="cond-ai-input"
              rows={2}
              placeholder="e.g. Nobody who can submit a price should also be able to publish it"
              value={instruction}
              disabled={draftMutation.isPending}
              onChange={(e) => setInstruction(e.target.value)}
            />
            <button
              type="button"
              className="mini-btn is-active"
              disabled={draftMutation.isPending || instruction.trim().length === 0}
              onClick={generateDraft}
            >
              <AppIcon name="sparkles" size={14} />{" "}
              {draftMutation.isPending ? "Drafting…" : "Draft with AI"}
            </button>
          </div>

          {draft && (
            <div className="sod-draft" aria-live="polite">
              <div className="sod-draft-matchers">
                <span className="sod-chip">{describeMatcher(draft.matcherA)}</span>
                <span className="sod-draft-vs">must not combine with</span>
                <span className="sod-chip">{describeMatcher(draft.matcherB)}</span>
                <StatusChip value={draft.severity} />
              </div>
              {draftIncomplete && (
                <p className="cond-ai-hint">
                  The draft is missing one or both sides — refine the description
                  and draft again before saving.
                </p>
              )}
              <div className="sod-draft-fields">
                <label className="field">
                  <span className="field-label">Rule key</span>
                  <input
                    value={ruleKey}
                    onChange={(e) => setRuleKey(e.target.value)}
                    placeholder="segregate-submit-publish"
                  />
                </label>
                <label className="field">
                  <span className="field-label">Name</span>
                  <input
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                  />
                </label>
              </div>
              <div className="sod-draft-actions">
                <button
                  type="button"
                  className="mini-btn is-active"
                  disabled={saveMutation.isPending || draftIncomplete}
                  onClick={saveDraft}
                >
                  {saveMutation.isPending ? "Saving…" : "Save rule"}
                </button>
                <button
                  type="button"
                  className="mini-btn ghost"
                  onClick={resetComposer}
                >
                  Cancel
                </button>
              </div>
            </div>
          )}

          <p className="cond-ai-hint">
            AI only drafts the two permission matchers from your description.
            Detection is deterministic and rules are validated before saving.
          </p>
        </div>
      )}

      {rules.length > 0 && (
        <div className="sod-rules" aria-label="Configured rules and findings">
          <span className="sod-rules-head muted">
            Configured rules ({rules.length})
          </span>
          <ul className="sod-rules-list">
            {rules.map((rule) => {
              const ruleViolations = violationsByRule.get(rule.ruleKey) ?? [];
              const count = ruleViolations.length;
              const open = isRuleOpen(rule.ruleKey, count > 0);
              const panelId = `sod-rule-${rule.ruleKey}`;
              return (
                <li key={rule.ruleKey} className="sod-rules-item">
                  <div className="sod-rule-row">
                    <button
                      type="button"
                      className="sod-rule-toggle"
                      aria-expanded={open}
                      aria-controls={panelId}
                      onClick={() => toggleRule(rule.ruleKey)}
                    >
                      <span
                        className={`sod-rule-chevron${open ? " is-open" : ""}`}
                      >
                        <AppIcon name="chevron" size={14} />
                      </span>
                      <span className="sod-rules-body">
                        <span className="sod-rules-title">
                          {rule.name}
                          <StatusChip value={rule.severity} />
                          {count > 0 ? (
                            <span className="sod-finding-count">
                              {count} finding{count === 1 ? "" : "s"}
                            </span>
                          ) : (
                            <span className="sod-finding-clear">
                              <AppIcon name="active" size={12} /> Clear
                            </span>
                          )}
                        </span>
                        <span className="sod-rules-matchers muted">
                          <span className="sod-chip">
                            {describeMatcher(rule.matcherA)}
                          </span>
                          <span className="sod-draft-vs">vs</span>
                          <span className="sod-chip">
                            {describeMatcher(rule.matcherB)}
                          </span>
                        </span>
                      </span>
                    </button>
                    {canAuthor && (
                      <button
                        type="button"
                        className="mini-btn ghost"
                        disabled={deleteMutation.isPending}
                        onClick={() => setPendingDelete(rule)}
                      >
                        Delete
                      </button>
                    )}
                  </div>
                  {open && (
                    <div id={panelId} className="sod-rule-findings">
                      {count > 0 ? (
                        <ul className="advisor-list">
                          {ruleViolations.map((violation, i) => (
                            <SodViolationItem
                              key={`${violation.ruleKey}-${i}`}
                              violation={violation}
                              showRuleName={false}
                              onSelect={onSelect}
                            />
                          ))}
                        </ul>
                      ) : (
                        <p className="sod-rule-empty muted">
                          No conflicts detected for this rule.
                        </p>
                      )}
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      )}

      {violationsQuery.isLoading ? (
        <Spinner label="Scanning for separation-of-duties conflicts…" />
      ) : violationsQuery.isError ? (
        <p className="muted">Could not load separation-of-duties findings.</p>
      ) : violations.length === 0 ? (
        <p className="advisor-empty">
          <AppIcon name="active" size={16} /> No separation-of-duties conflicts
          detected against the active rules.
        </p>
      ) : ungroupedViolations.length > 0 ? (
        <div className="sod-ungrouped">
          <span className="sod-rules-head muted">Findings</span>
          <ul className="advisor-list advisor-list-scroll">
            {ungroupedViolations.map((violation, i) => (
              <SodViolationItem
                key={`${violation.ruleKey}-${i}`}
                violation={violation}
                showRuleName
                onSelect={onSelect}
              />
            ))}
          </ul>
        </div>
      ) : null}

      <ConfirmDialog
        open={pendingDelete !== null}
        title="Delete separation-of-duties rule"
        message={
          pendingDelete
            ? `Delete "${pendingDelete.name}"? It will stop being detected and drop out of the rule list. This cannot be undone from the UI.`
            : ""
        }
        confirmLabel="Delete rule"
        danger
        confirmDisabled={deleteMutation.isPending}
        onConfirm={confirmDelete}
        onCancel={() => setPendingDelete(null)}
      />
    </section>
  );
}
