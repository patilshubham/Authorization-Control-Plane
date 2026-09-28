import { useMemo, useState } from "react";
import { useSimulate, useExplainDecision } from "../../api/hooks";
import { useAiFeature } from "../../api/aiConfig";
import { userFacingError } from "../../apiClient";
import type { SimulatorDecision } from "../../types";
import { Field } from "../../components/primitives";
import { AppIcon } from "../../components/icons";
import { useToast } from "../../components/Toast";
import { ContextBuilder } from "../simulator/ContextBuilder";
import { D3Tree } from "../../components/viz/D3Tree";
import { buildDecisionPath } from "../../components/viz/graphModel";
import {
  emptyContext,
  serializeContext,
  validate,
  type CtxNode,
} from "../simulator/contextModel";

// ── Simulator ────────────────────────────────────────────────────────────────────

export function SimulatorPanel({ appId }: { appId: string }) {
  const simulate = useSimulate();
  const [subjectEmail, setSubjectEmail] = useState("");
  const [resourceType, setResourceType] = useState("");
  const [resourceId, setResourceId] = useState("");
  const [action, setAction] = useState("");
  const [context, setContext] = useState<CtxNode>(() => emptyContext());
  const [result, setResult] = useState<SimulatorDecision | null>(null);

  const contextErrors = useMemo(() => validate(context), [context]);

  const run = () => {
    if (contextErrors.length > 0) return;
    simulate.mutate(
      {
        applicationId: appId,
        subjectEmail,
        resourceType,
        resourceId,
        action,
        context: serializeContext(context),
      },
      { onSuccess: setResult },
    );
  };

  return (
    <article className="inspector">
      <header className="inspector-head">
        <div className="inspector-title-row">
          <h1>Authorization simulator</h1>
        </div>
        <p className="inspector-key">
          Evaluate a decision against live roles, mappings and policies.
        </p>
      </header>

      <section className="inspector-block sim-grid">
        <Field label="Subject email">
          <input
            value={subjectEmail}
            onChange={(e) => setSubjectEmail(e.target.value)}
          />
        </Field>
        <Field label="Resource type">
          <input
            value={resourceType}
            onChange={(e) => setResourceType(e.target.value)}
          />
        </Field>
        <Field label="Resource ID">
          <input
            value={resourceId}
            onChange={(e) => setResourceId(e.target.value)}
          />
        </Field>
        <Field label="Action">
          <input value={action} onChange={(e) => setAction(e.target.value)} />
        </Field>
      </section>

      <section className="inspector-block">
        <Field
          label="Request context"
          hint="Attributes your policy conditions evaluate. Build them visually — no JSON required."
        >
          <ContextBuilder value={context} onChange={setContext} />
        </Field>
      </section>

      <div className="inspector-action-bar">
        <button
          type="button"
          className="btn-primary"
          disabled={
            simulate.isPending ||
            !subjectEmail ||
            !action ||
            contextErrors.length > 0
          }
          onClick={run}
        >
          {simulate.isPending ? "Evaluating…" : "Evaluate"}
        </button>
      </div>

      {result && (
        <section
          className={`sim-result ${result.allowed ? "allowed" : "denied"}`}
        >
          <p className="sim-verdict">{result.allowed ? "ALLOWED" : "DENIED"}</p>
          {result.denyReason && (
            <p className="muted">Reason: {result.denyReason}</p>
          )}
          {result.reason && (
            <dl className="detail-grid">
              <div>
                <dt>Matched roles</dt>
                <dd>{result.reason.matchedRoles.join(", ") || "—"}</dd>
              </div>
              <div>
                <dt>Matched permissions</dt>
                <dd>{result.reason.matchedPermissions.join(", ") || "—"}</dd>
              </div>
              <div>
                <dt>Matched policies</dt>
                <dd>{result.reason.matchedPolicies.join(", ") || "—"}</dd>
              </div>
            </dl>
          )}
          {result.obligations && result.obligations.length > 0 && (
            <div className="sim-obligations">
              <span className="sim-path-head muted">Obligations</span>
              <div className="chip-row">
                {result.obligations.map((o) => (
                  <span key={o.id} className="chip">
                    {o.value ? `${o.id} = ${o.value}` : o.id}
                  </span>
                ))}
              </div>
            </div>
          )}
          <div className="sim-path">
            <span className="sim-path-head muted">Decision path</span>
            <D3Tree
              data={buildDecisionPath(
                subjectEmail,
                result.allowed,
                result.reason,
                result.denyReason,
              )}
              height={260}
              ariaLabel="Decision path"
            />
          </div>
          <p className="muted">Decision ID: {result.decisionId}</p>
          <AiExplainDecision
            key={result.decisionId}
            appId={appId}
            subjectEmail={subjectEmail}
            resourceType={resourceType}
            resourceId={resourceId}
            action={action}
            context={serializeContext(context)}
            decision={result}
          />
        </section>
      )}
    </article>
  );
}

type AiExplainDecisionProps = {
  appId: string;
  subjectEmail: string;
  resourceType: string;
  resourceId: string;
  action: string;
  context: Record<string, unknown>;
  decision: SimulatorDecision;
};

/**
 * F2 — Decision Explainer. Produces a plain-language narrative for a decision the
 * deterministic engine already made. Advisory only; only mounted when the feature is on.
 */
function AiExplainDecision({
  appId,
  subjectEmail,
  resourceType,
  resourceId,
  action,
  context,
  decision,
}: AiExplainDecisionProps) {
  const enabled = useAiFeature("decisionExplainer");
  const explain = useExplainDecision();
  const toast = useToast();
  const [narrative, setNarrative] = useState<string | null>(null);
  const [remediation, setRemediation] = useState<string[]>([]);

  const contextEntries = useMemo(() => Object.entries(context), [context]);

  const inputPayload = useMemo(
    () => ({
      subject: { email: subjectEmail || null },
      resource: { type: resourceType || null, id: resourceId || null },
      action: action || null,
      context,
    }),
    [subjectEmail, resourceType, resourceId, action, context],
  );

  if (!enabled) return null;

  async function run() {
    // Clear the previous explanation up front so stale narrative/remediation
    // never lingers if the new request fails.
    setNarrative(null);
    setRemediation([]);
    try {
      const result = await explain.mutateAsync({
        applicationId: appId,
        allowed: decision.allowed,
        denyReason: decision.denyReason,
        subjectEmail,
        resourceType,
        resourceId,
        action,
        contextKeys: Object.keys(context),
        matchedRoles: decision.reason?.matchedRoles ?? [],
        matchedPermissions: decision.reason?.matchedPermissions ?? [],
        matchedPolicies: decision.reason?.matchedPolicies ?? [],
      });
      setNarrative(result.narrative);
      setRemediation(result.remediation);
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  return (
    <div className="ai-explain">
      <button
        type="button"
        className="mini-btn is-active"
        disabled={explain.isPending}
        onClick={run}
      >
        <AppIcon name="sparkles" size={14} />{" "}
        {explain.isPending
          ? "Explaining…"
          : narrative
            ? "Regenerate explanation"
            : "Explain this decision"}
      </button>
      {narrative && (
        <ExplanationCard
          allowed={decision.allowed}
          denyReason={decision.denyReason}
          decisionId={decision.decisionId}
          subjectEmail={subjectEmail}
          resourceType={resourceType}
          resourceId={resourceId}
          action={action}
          contextEntries={contextEntries}
          reason={decision.reason}
          obligations={decision.obligations ?? []}
          input={inputPayload}
          narrative={narrative}
          remediation={remediation}
        />
      )}
    </div>
  );
}

type ExplanationCardProps = {
  allowed: boolean;
  denyReason: string | null;
  decisionId: string;
  subjectEmail: string;
  resourceType: string;
  resourceId: string;
  action: string;
  contextEntries: [string, unknown][];
  reason: SimulatorDecision["reason"];
  obligations: NonNullable<SimulatorDecision["obligations"]>;
  input: Record<string, unknown>;
  narrative: string;
  remediation: string[];
};

/**
 * Structured, progressively-disclosed rendering of a decision explanation. The
 * deterministic facts (request, grants, obligations) frame the AI narrative so
 * the reader can trace the request → policy → outcome flow at a glance.
 */
function ExplanationCard({
  allowed,
  denyReason,
  decisionId,
  subjectEmail,
  resourceType,
  resourceId,
  action,
  contextEntries,
  reason,
  obligations,
  input,
  narrative,
  remediation,
}: ExplanationCardProps) {
  const roles = reason?.matchedRoles ?? [];
  const permissions = reason?.matchedPermissions ?? [];
  const policies = reason?.matchedPolicies ?? [];
  const subject = subjectEmail || "The subject";
  const resourceLabel = resourceId
    ? `${resourceType} “${resourceId}”`
    : resourceType || "the resource";

  const paragraphs = narrative
    .split(/\n{2,}|\r?\n/)
    .map((p) => p.trim())
    .filter(Boolean);

  const verdictSummary = allowed
    ? `${subject} is permitted to ${action || "act"} on ${resourceLabel}.`
    : `${subject} is not permitted to ${action || "act"} on ${resourceLabel}.`;

  const inputJson = JSON.stringify(input, null, 2);
  const outputJson = JSON.stringify(
    {
      decisionId,
      allowed,
      denyReason,
      matchedRoles: roles,
      matchedPermissions: permissions,
      matchedPolicies: policies,
      obligations,
    },
    null,
    2,
  );

  const takeaways = buildTakeaways({
    allowed,
    roles,
    permissions,
    policies,
    obligations,
    contextEntries,
    remediation,
  });

  return (
    <article
      className={`ai-explain-card ai-explain-${allowed ? "allow" : "deny"}`}
      aria-live="polite"
    >
      <header className="ai-explain-header">
        <span className="ai-explain-avatar">
          <AppIcon name="sparkles" size={16} />
        </span>
        <div className="ai-explain-heading">
          <h3>AI decision explanation</h3>
          <span className="ai-explain-sub">
            Interpreting the deterministic engine result
          </span>
        </div>
        <span className="ai-explain-tag">Advisory</span>
      </header>

      {/* Decision — surfaced first */}
      <div className={`ai-verdict ai-verdict-${allowed ? "allow" : "deny"}`}>
        <span className="ai-verdict-icon">
          <AppIcon name={allowed ? "active" : "denied"} size={26} />
        </span>
        <div className="ai-verdict-text">
          <strong>{allowed ? "Access allowed" : "Access denied"}</strong>
          <span>{verdictSummary}</span>
          {!allowed && denyReason && (
            <span className="ai-verdict-reason">Engine reason: {denyReason}</span>
          )}
        </div>
      </div>

      {/* Authorization flow — request → grants → policies → outcome */}
      <div className="ai-flow" role="list" aria-label="Authorization flow">
        <FlowStep icon="users" label="Subject" value={subjectEmail || "—"} />
        <FlowArrow />
        <FlowStep
          icon="assignments"
          label="Roles"
          value={roles.length ? `${roles.length} matched` : "None"}
          muted={roles.length === 0}
        />
        <FlowArrow />
        <FlowStep
          icon="permissions"
          label="Permission"
          value={permissions[0] ?? action ?? "—"}
          muted={permissions.length === 0}
        />
        <FlowArrow />
        <FlowStep
          icon="policies"
          label="Policies"
          value={policies.length ? `${policies.length} applied` : "None"}
          muted={policies.length === 0}
        />
        <FlowArrow />
        <div
          role="listitem"
          className={`ai-flow-step ai-flow-outcome ai-flow-${allowed ? "allow" : "deny"}`}
        >
          <span className="ai-flow-icon">
            <AppIcon name={allowed ? "active" : "denied"} size={16} />
          </span>
          <div className="ai-flow-body">
            <span className="ai-flow-label">Outcome</span>
            <span className="ai-flow-value">{allowed ? "Allow" : "Deny"}</span>
          </div>
        </div>
      </div>

      {/* Request summary */}
      <section className="ai-section">
        <h4 className="ai-section-title">
          <AppIcon name="lens" size={14} /> Request summary
        </h4>
        <dl className="ai-facts">
          <div>
            <dt>Subject</dt>
            <dd>{subjectEmail || "—"}</dd>
          </div>
          <div>
            <dt>Action</dt>
            <dd>
              <code>{action || "—"}</code>
            </dd>
          </div>
          <div>
            <dt>Resource type</dt>
            <dd>{resourceType || "—"}</dd>
          </div>
          <div>
            <dt>Resource ID</dt>
            <dd>{resourceId || "—"}</dd>
          </div>
        </dl>
        {contextEntries.length > 0 && (
          <div className="ai-context">
            <span className="ai-context-label">Context attributes</span>
            <div className="ai-attr-row">
              {contextEntries.map(([key, value]) => (
                <span key={key} className="ai-attr">
                  <b>{key}</b>
                  <span>{formatAttr(value)}</span>
                </span>
              ))}
            </div>
          </div>
        )}
      </section>

      {/* Policies & grants evaluated */}
      <section className="ai-section">
        <h4 className="ai-section-title">
          <AppIcon name="policies" size={14} /> Policies &amp; grants evaluated
        </h4>
        <div className="ai-eval-grid">
          <EvalGroup
            label="Roles held"
            items={roles}
            emptyLabel="No roles matched the subject"
          />
          <EvalGroup
            label="Permissions granted"
            items={permissions}
            emptyLabel="No permissions granted"
          />
          <EvalGroup
            label="Policies applied"
            items={policies}
            emptyLabel="No conditional policies applied"
            tone={allowed ? "allow" : "deny"}
          />
        </div>
      </section>

      {/* Why this decision was made — the AI narrative */}
      <section className="ai-section">
        <h4 className="ai-section-title">
          <AppIcon name="info" size={14} /> Why this decision was made
        </h4>
        <div className="ai-narrative">
          {paragraphs.length > 0 ? (
            paragraphs.map((text, index) => <p key={index}>{text}</p>)
          ) : (
            <p>{narrative}</p>
          )}
        </div>
      </section>

      {/* Obligations */}
      {obligations.length > 0 && (
        <section className="ai-section">
          <h4 className="ai-section-title">
            <AppIcon name="reference" size={14} /> Obligations to enforce
          </h4>
          <p className="ai-section-note">
            The caller must honour these on an allow before completing the action.
          </p>
          <div className="chip-row">
            {obligations.map((o) => (
              <span key={o.id} className="chip chip-granted">
                {o.value ? `${o.id} = ${o.value}` : o.id}
              </span>
            ))}
          </div>
        </section>
      )}

      {/* Request & decision payloads — side by side, as sent/returned by the PDP */}
      <section className="ai-section">
        <h4 className="ai-section-title">
          <AppIcon name="code" size={14} /> Request &amp; decision payload
        </h4>
        <p className="ai-section-note">
          The JSON your service sends to the authorization API and the decision
          it returns.
        </p>
        <div className="ai-json-grid">
          <figure className="ai-json-block">
            <figcaption className="ai-json-head">
              <span className="ai-json-tag ai-json-in">Input</span>
              authorization request
            </figcaption>
            <pre className="ai-json">
              <code>{inputJson}</code>
            </pre>
          </figure>
          <figure className="ai-json-block">
            <figcaption className="ai-json-head">
              <span
                className={`ai-json-tag ai-json-out ai-json-out-${allowed ? "allow" : "deny"}`}
              >
                Output
              </span>
              decision response
            </figcaption>
            <pre className="ai-json">
              <code>{outputJson}</code>
            </pre>
          </figure>
        </div>
      </section>

      {/* Key takeaways */}
      <section className={`ai-callout ai-callout-${allowed ? "allow" : "deny"}`}>
        <h4 className="ai-callout-title">
          <AppIcon name="bulb" size={15} /> Key takeaways
        </h4>
        <ul className="ai-takeaways">
          {takeaways.map((item, index) => (
            <li key={index}>{item}</li>
          ))}
        </ul>
      </section>

      <p className="cond-ai-hint">AI-generated explanation — advisory only.</p>
    </article>
  );
}

function FlowStep({
  icon,
  label,
  value,
  muted,
}: {
  icon: string;
  label: string;
  value: string;
  muted?: boolean;
}) {
  return (
    <div
      role="listitem"
      className={`ai-flow-step${muted ? " ai-flow-step-muted" : ""}`}
    >
      <span className="ai-flow-icon">
        <AppIcon name={icon} size={16} />
      </span>
      <div className="ai-flow-body">
        <span className="ai-flow-label">{label}</span>
        <span className="ai-flow-value">{value}</span>
      </div>
    </div>
  );
}

function FlowArrow() {
  return (
    <span className="ai-flow-arrow" aria-hidden="true">
      <AppIcon name="chevron" size={14} />
    </span>
  );
}

function EvalGroup({
  label,
  items,
  emptyLabel,
  tone,
}: {
  label: string;
  items: string[];
  emptyLabel: string;
  tone?: "allow" | "deny";
}) {
  return (
    <div className="ai-eval-group">
      <span className="ai-eval-label">{label}</span>
      {items.length > 0 ? (
        <div className="chip-row">
          {items.map((item) => (
            <span
              key={item}
              className={`chip${tone === "allow" ? " chip-granted" : ""}${
                tone === "deny" ? " chip-danger" : ""
              }`}
            >
              {item}
            </span>
          ))}
        </div>
      ) : (
        <span className="ai-eval-empty">{emptyLabel}</span>
      )}
    </div>
  );
}

function formatAttr(value: unknown): string {
  if (value === null || value === undefined) return "—";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}

function buildTakeaways({
  allowed,
  roles,
  permissions,
  policies,
  obligations,
  contextEntries,
  remediation,
}: {
  allowed: boolean;
  roles: string[];
  permissions: string[];
  policies: string[];
  obligations: { id: string; value: string | null }[];
  contextEntries: [string, unknown][];
  remediation: string[];
}): string[] {
  const items: string[] = [];
  const ctxSummary = contextEntries
    .map(([key, value]) => `${key} = ${formatAttr(value)}`)
    .join(", ");
  const ctxSuffix = ctxSummary
    ? ` for the current context (${ctxSummary})`
    : "";
  if (allowed) {
    if (roles.length > 0 && permissions.length > 0) {
      items.push(
        `The subject's ${roles.length === 1 ? "role" : "roles"} (${roles.join(
          ", ",
        )}) grant the required permission${permissions.length === 1 ? "" : "s"}.`,
      );
    }
    if (policies.length > 0) {
      policies.forEach((policy) => {
        items.push(
          `Policy “${policy}” matched and its conditions were satisfied${ctxSuffix}, so it contributed an allow.`,
        );
      });
    } else {
      items.push(
        "No conditional policy restricted the request, so the role grant stands.",
      );
    }
    if (obligations.length > 0) {
      items.push(
        `Enforce ${obligations.length} ${
          obligations.length === 1 ? "obligation" : "obligations"
        } before completing the action.`,
      );
    }
  } else {
    if (remediation.length > 0) {
      remediation.forEach((step) => items.push(step));
    }
    if (permissions.length === 0) {
      items.push(
        "The subject holds no role that grants the requested permission.",
      );
    }
    if (policies.length > 0) {
      policies.forEach((policy) => {
        items.push(
          `Policy “${policy}” matched and denied the request${ctxSuffix}.`,
        );
      });
    }
  }
  if (items.length === 0) {
    items.push(
      allowed
        ? "The request satisfied every applicable rule."
        : "The request did not satisfy the required rules.",
    );
  }
  return items;
}
