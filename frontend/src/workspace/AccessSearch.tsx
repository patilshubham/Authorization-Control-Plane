import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { usePlatformAccessSearch } from "../api/hooks";
import { useAiFeature } from "../api/aiConfig";
import { AppIcon } from "../components/icons";
import { Spinner } from "../components/primitives";
import { CopyButton, DrawerPanel } from "../ui";
import {
  type AccessSearchResponse,
  type AccessSearchResult,
} from "../apiClient";
import { appPaths, platformPaths } from "./nav";

// F8 — Natural-language access search. The model only ever plans a closed,
// validated query spec (entity + filters over a fixed field allow-list); the API
// executes it with parameterized, read-only queries. Every result carries a
// deep-link citation so the answer is auditable, not a black box.

// Generic, goal-based starter prompts — reusable across any application and free of
// app-, role-, or data-specific names. Each maps to a query the closed engine supports
// (entity + attribute filters), so a click always returns a real answer.
const SUGGESTIONS = [
  "Which roles are privileged?",
  "Which policies deny access?",
  "Which permissions are high risk?",
  "Which applications are high risk?",
];

// The six capability groups the closed query engine supports, described in
// plain language with a worked example each — shown in the "About Ask AI"
// drawer so administrators know what kinds of questions are actually
// answerable before they type one. Each example is clickable: it fills the
// search box, runs, and closes the drawer.
const CAPABILITIES: {
  id: string;
  title: string;
  icon: string;
  desc: string;
  examples: string[];
}[] = [
  {
    id: "search",
    title: "Search & filter",
    icon: "search",
    desc: "Look up roles, permissions, policies, applications, assignments, tenants, or users that match plain-language criteria — status, risk, effect, state, and more.",
    examples: ["Which roles are privileged?", "Which policies deny access?"],
  },
  {
    id: "count",
    title: "Count & measure",
    icon: "activity",
    desc: "Get a single, auditable number — with the matching records shown right behind it, not just a black-box total.",
    examples: [
      "How many roles grant no permissions?",
      "How many assignments are in state EXPIRED?",
    ],
  },
  {
    id: "group",
    title: "Group & compare",
    icon: "matrix",
    desc: "Break results into ranked groups, with a second-level breakdown when it helps (e.g. by risk, then by status) — the same breakdown doubles as a top-N ranking.",
    examples: [
      "Group applications by risk level",
      "Group roles by risk, then by status",
    ],
  },
  {
    id: "relate",
    title: "Explore relationships",
    icon: "assignments",
    desc: "Follow a record to what it's connected to — a role to its permissions, a permission to the policies that reference it, a user to the roles and applications they can reach.",
    examples: [
      "Show a role and its permissions",
      "Show a user's roles and permissions",
    ],
  },
  {
    id: "gaps",
    title: "Identify governance gaps",
    icon: "empty",
    desc: "Surface unused permissions, unassigned roles, and other configuration gaps within an application — before they become an audit finding.",
    examples: [
      "Show unused permissions for an application",
      "Which roles have no assignments?",
    ],
  },
  {
    id: "activity",
    title: "Review activity & decisions",
    icon: "audit",
    desc: "Look at authorization decisions (allow/deny), audit events, and Ask AI's own request history.",
    examples: ["Show denied authorization decisions", "Show recent audit events"],
  },
];

// Single-hop relationships Ask AI can traverse, rendered as compact chains of
// entity chips rather than a bare list — mirrors the platform's real
// hierarchy (a tenant owns applications; each owns its roles, permissions,
// policies, and assignments).
const RELATIONSHIP_CHAINS: { label: string; icon: string }[][] = [
  [
    { label: "Role", icon: "roles" },
    { label: "Permissions", icon: "permissions" },
  ],
  [
    { label: "Permission", icon: "permissions" },
    { label: "Policies", icon: "policies" },
  ],
  [
    { label: "User", icon: "users" },
    { label: "Roles", icon: "roles" },
    { label: "Permissions", icon: "permissions" },
  ],
  [
    { label: "Application", icon: "applications" },
    { label: "Roles", icon: "roles" },
    { label: "Assignments", icon: "assignments" },
  ],
  [
    { label: "Tenant", icon: "tenants" },
    { label: "Applications", icon: "applications" },
    { label: "Roles", icon: "roles" },
  ],
];

// Questions that span three or more connected entities in a single request —
// shown as their own clickable prompt cards since they're the clearest proof
// of what "explore relationships" means in practice.
const MULTI_LEVEL_EXAMPLES: string[] = [
  "Show a role together with its permissions and the policies attached to those permissions.",
  "Show a user, the roles assigned to them, and the permissions those roles grant.",
  "Show each application together with its roles and the policies that apply to them.",
  "Show each tenant together with its applications and the roles defined within each.",
  "Show the hierarchy from tenant through applications, roles, and permissions.",
];

// The four-step pipeline behind every answer — deliberately not "the model runs
// your query": the model only ever produces a closed plan, which is validated
// and executed by ordinary, parameterized backend code. Shown in the drawer so
// admins understand *why* the answers are safe and auditable, not just that
// they are.
const HOW_IT_WORKS: { title: string; icon: string; desc: string }[] = [
  {
    title: "Interpret",
    icon: "sparkles",
    desc: "Your question is turned into a structured plan — an entity (e.g. role, policy) plus a small set of allow-listed filters. The model never touches your data directly.",
  },
  {
    title: "Validate",
    icon: "lock",
    desc: "The plan is checked against a closed schema. Only known entities, fields, and operators are accepted — anything else is rejected before it runs.",
  },
  {
    title: "Execute",
    icon: "activity",
    desc: "The API runs the plan as parameterized, read-only queries, scoped to what you're permitted to see. Nothing is ever generated as free-form code, and access can never be changed.",
  },
  {
    title: "Cite",
    icon: "reference",
    desc: "Every result links back to the real record (role, policy, application…), so the answer can always be verified against the source — never taken on faith.",
  },
];

// Actionable habits that get better answers faster — synthesized from the
// mechanics above, not new capabilities.
const TIPS: string[] = [
  "Name the entity you care about — role, permission, policy, application, tenant, or user — for the fastest, most precise match.",
  "Ask for a relationship explicitly (e.g. “…and its permissions”) to get a connected, multi-level answer in one question.",
  "If a question can't be answered, Ask AI says so and suggests a rephrase — it never guesses.",
  "Every answer links back to its source record — always verify before you act on it.",
];

// The read-only guarantees behind every answer.
const SECURITY_POINTS: string[] = [
  "Every query runs as a parameterized, read-only operation — nothing is ever generated as free-form code.",
  "Only known entities, fields, and operators are accepted; anything else is rejected before it runs.",
  "Access is never changed. Ask AI can read and explain — it cannot grant, revoke, or modify anything.",
  "Results are scoped to what you're permitted to see — never data outside your access.",
  "Every result links back to its real source record, so any answer can be verified, not taken on faith.",
  "Ambiguous or unanswerable questions return guidance to help you rephrase — never a fabricated answer.",
];

// Categorized example prompts shown in a searchable, collapsible accordion.
// Every prompt below is phrased in a shape the query engine actually
// understands — none are aspirational.
const EXAMPLE_CATEGORIES: { id: string; title: string; icon: string; prompts: string[] }[] = [
  {
    id: "search-filter",
    title: "Search & filter",
    icon: "search",
    prompts: [
      "Which roles are privileged?",
      "Which policies deny access?",
      "Which permissions are high risk?",
      "Which applications are high risk?",
      "Show roles in status DISABLED.",
      "Show assignments in state EXPIRED.",
      "Show policies for a specific permission.",
    ],
  },
  {
    id: "counts",
    title: "Counts & metrics",
    icon: "activity",
    prompts: [
      "How many roles grant no permissions?",
      "How many policies are still drafts?",
      "How many assignments are in state EXPIRED?",
      "How many permissions are not in status ACTIVE?",
    ],
  },
  {
    id: "grouping",
    title: "Grouping & summaries",
    icon: "matrix",
    prompts: [
      "Group applications by risk level.",
      "Group roles by risk, then by status.",
      "Group policies by effect.",
      "Group assignments by state.",
    ],
  },
  {
    id: "relationships",
    title: "Relationship analysis",
    icon: "assignments",
    prompts: [
      "Show a role and its permissions.",
      "Which policies reference a given permission?",
      "Which roles grant a given permission?",
      "Show the roles and permissions assigned to a user.",
      "Which applications can a user access?",
      "Show an application's roles, permissions, and policies.",
      "Show a tenant's applications and roles.",
    ],
  },
  {
    id: "multi-level",
    title: "Multi-level relationship queries",
    icon: "reference",
    prompts: MULTI_LEVEL_EXAMPLES,
  },
  {
    id: "governance",
    title: "Governance & gap analysis",
    icon: "empty",
    prompts: [
      "Show unused permissions for an application.",
      "Which roles have no assignments?",
      "Which roles grant no permissions?",
      "Show segregation-of-duties rules for an application.",
      "Show active access review campaigns.",
      "Show identity providers configured for an application.",
    ],
  },
  {
    id: "ranking",
    title: "Ranking & comparison",
    icon: "overview",
    prompts: [
      "Which roles grant the most permissions?",
      "Which applications have the most unused permissions?",
      "Which permissions are granted by the most roles?",
      "Rank applications by risk level.",
    ],
  },
  {
    id: "operational",
    title: "Operational insights",
    icon: "audit",
    prompts: [
      "Show denied authorization decisions.",
      "Show recent audit events.",
      "Show recent Ask AI requests.",
      "How many authorization decisions were denied?",
      "Show allowed decisions for a specific resource.",
    ],
  },
];

const SUPPORTED_SCOPE: string[] = [
  "Roles, permissions, policies, applications, assignments, tenants, and users",
  "Governance data — segregation-of-duties rules, identity providers, review campaigns, reference data",
  "Authorization decisions and audit activity, including Ask AI's own request history",
  "Relationships and multi-level connections between all of the above",
  "Counts, groupings, and rankings over any of this data",
];

const UNSUPPORTED_SCOPE: string[] = [
  "General knowledge or topics unrelated to this platform's authorization data",
  "Requests to create, change, or delete access — Ask AI is strictly read-only",
  "Open-ended questions outside the closed set of entities and fields above — Ask AI explains why and suggests a rephrase",
];

// Sticky in-drawer navigation — anchors into the sections below.
const NAV_SECTIONS: { id: string; label: string }[] = [
  { id: "ask-ai-capabilities", label: "Capabilities" },
  { id: "ask-ai-relationships", label: "Relationships" },
  { id: "ask-ai-how-it-works", label: "How it works" },
  { id: "ask-ai-security", label: "Security" },
  { id: "ask-ai-examples", label: "Examples" },
  { id: "ask-ai-scope", label: "Scope" },
];

/** Rich reference panel for the Ask AI feature — what it is, the kinds of
 * questions it can answer (with clickable, worked examples), how the
 * interpret → validate → execute → cite pipeline keeps answers grounded and
 * auditable, and where its boundaries are. Opened from a small trigger next
 * to the search bar so the search box itself stays the focus of the page. */
function AskAiHelpDrawer({
  open,
  onClose,
  onUseQuestion,
}: {
  open: boolean;
  onClose: () => void;
  onUseQuestion: (question: string) => void;
}) {
  const [openCategories, setOpenCategories] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState("");
  const normalizedFilter = filter.trim().toLowerCase();

  const filteredCategories = useMemo(() => {
    if (!normalizedFilter) return EXAMPLE_CATEGORIES;
    return EXAMPLE_CATEGORIES.map((c) => ({
      ...c,
      prompts: c.prompts.filter((p) => p.toLowerCase().includes(normalizedFilter)),
    })).filter((c) => c.prompts.length > 0);
  }, [normalizedFilter]);

  function expandAll() {
    setOpenCategories(new Set(EXAMPLE_CATEGORIES.map((c) => c.id)));
  }
  function collapseAll() {
    setOpenCategories(new Set());
  }

  return (
    <DrawerPanel title="About Ask AI" open={open} onClose={onClose} wide>
      <div className="ask-ai-help">
        <p className="ask-ai-help-lead">
          Ask AI is your assistant for exploring authorization across the
          platform. Ask in plain language and it translates your question into
          safe, read-only queries over roles, permissions, policies,
          applications, assignments, governance data, and the relationships
          between them — returning answers with deep-link citations you can
          verify.
        </p>

        <nav className="ask-ai-nav" aria-label="Jump to section">
          {NAV_SECTIONS.map((s) => (
            <a key={s.id} href={`#${s.id}`} className="ask-ai-nav-link">
              {s.label}
            </a>
          ))}
        </nav>

        <section id="ask-ai-capabilities" className="ask-ai-section">
          <div className="ask-ai-section-head">
            <h3>What you can ask</h3>
          </div>
          <div className="ask-ai-cap-grid">
            {CAPABILITIES.map((c) => (
              <div key={c.id} className="ask-ai-cap-card">
                <span className="ask-ai-cap-icon" aria-hidden="true">
                  <AppIcon name={c.icon} size={16} />
                </span>
                <span className="ask-ai-cap-title">{c.title}</span>
                <p className="ask-ai-cap-desc">{c.desc}</p>
                <div className="ask-ai-cap-examples">
                  {c.examples.map((e) => (
                    <button
                      key={e}
                      type="button"
                      className="ask-ai-example-chip"
                      onClick={() => onUseQuestion(e)}
                    >
                      {e}
                    </button>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </section>

        <section id="ask-ai-relationships" className="ask-ai-section">
          <div className="ask-ai-section-head">
            <h3>Explore relationships</h3>
            <p className="ask-ai-section-desc">
              A single question can follow a record to what it's connected to
              — spanning several linked entities in one answer.
            </p>
          </div>
          <div className="ask-ai-chain-list">
            {RELATIONSHIP_CHAINS.map((chain, i) => (
              <div key={i} className="ask-ai-chain">
                {chain.map((n, j) => (
                  <span key={n.label} className="ask-ai-chain-item">
                    <span className="ask-ai-chain-node">
                      <AppIcon name={n.icon} size={13} />
                      {n.label}
                    </span>
                    {j < chain.length - 1 && (
                      <span className="ask-ai-chain-arrow" aria-hidden="true">
                        →
                      </span>
                    )}
                  </span>
                ))}
              </div>
            ))}
          </div>
          <h4 className="ask-ai-subhead">Multi-level examples</h4>
          <div className="ask-ai-prompt-list">
            {MULTI_LEVEL_EXAMPLES.map((p) => (
              <div key={p} className="ask-ai-prompt-card">
                <span className="ask-ai-prompt-text">&ldquo;{p}&rdquo;</span>
                <div className="ask-ai-prompt-actions">
                  <button
                    type="button"
                    className="mini-btn ghost"
                    onClick={() => onUseQuestion(p)}
                  >
                    Use
                  </button>
                  <CopyButton
                    value={p}
                    label="Copy"
                    copiedLabel="Copied"
                    className="mini-btn ghost"
                  />
                </div>
              </div>
            ))}
          </div>
        </section>

        <section id="ask-ai-how-it-works" className="ask-ai-section">
          <div className="ask-ai-section-head">
            <h3>How it works</h3>
          </div>
          <ol className="ask-ai-timeline">
            {HOW_IT_WORKS.map((s) => (
              <li key={s.title} className="ask-ai-timeline-item">
                <span className="ask-ai-timeline-marker" aria-hidden="true">
                  <AppIcon name={s.icon} size={14} />
                </span>
                <div className="ask-ai-timeline-body">
                  <span className="ask-ai-timeline-title">{s.title}</span>
                  <p className="ask-ai-timeline-desc">{s.desc}</p>
                </div>
              </li>
            ))}
          </ol>
        </section>

        <section className="ask-ai-section">
          <div className="ask-ai-section-head">
            <h3>Tips for better answers</h3>
          </div>
          <ul className="ask-ai-tips-list">
            {TIPS.map((t) => (
              <li key={t} className="ask-ai-tip">
                <AppIcon name="bulb" size={14} />
                <span>{t}</span>
              </li>
            ))}
          </ul>
        </section>

        <section id="ask-ai-security" className="ask-ai-section">
          <div className="ask-ai-callout ask-ai-callout-success">
            <div className="ask-ai-callout-head">
              <AppIcon name="lock" size={16} />
              <h3>Security &amp; trust</h3>
            </div>
            <ul className="ask-ai-callout-list">
              {SECURITY_POINTS.map((p) => (
                <li key={p}>{p}</li>
              ))}
            </ul>
          </div>
        </section>

        <section id="ask-ai-examples" className="ask-ai-section">
          <div className="ask-ai-section-head">
            <h3>Example questions</h3>
          </div>
          <div className="ask-ai-examples-toolbar">
            <label className="ask-ai-filter">
              <AppIcon name="search" size={14} />
              <input
                type="search"
                placeholder="Filter examples…"
                aria-label="Filter example questions"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
              />
            </label>
            <div className="ask-ai-examples-actions">
              <button type="button" className="mini-btn ghost" onClick={expandAll}>
                Expand all
              </button>
              <button
                type="button"
                className="mini-btn ghost"
                onClick={collapseAll}
              >
                Collapse all
              </button>
            </div>
          </div>
          {filteredCategories.length === 0 ? (
            <p className="ask-ai-empty-filter muted">
              No examples match &ldquo;{filter}&rdquo;.
            </p>
          ) : (
            <div className="ask-ai-categories">
              {filteredCategories.map((c) => (
                <details
                  key={c.id}
                  className="ask-ai-category"
                  open={normalizedFilter.length > 0 || openCategories.has(c.id)}
                  onToggle={(e) => {
                    if (normalizedFilter.length > 0) return;
                    const isOpen = (e.target as HTMLDetailsElement).open;
                    setOpenCategories((prev) => {
                      const next = new Set(prev);
                      if (isOpen) next.add(c.id);
                      else next.delete(c.id);
                      return next;
                    });
                  }}
                >
                  <summary className="ask-ai-category-summary">
                    <AppIcon name={c.icon} size={14} />
                    <span className="ask-ai-category-title">{c.title}</span>
                    <span className="ask-ai-category-count">
                      {c.prompts.length}
                    </span>
                  </summary>
                  <div className="ask-ai-category-body">
                    {c.prompts.map((p) => (
                      <div key={p} className="ask-ai-prompt-row">
                        <span className="ask-ai-prompt-row-text">{p}</span>
                        <div className="ask-ai-prompt-actions">
                          <button
                            type="button"
                            className="mini-btn ghost"
                            onClick={() => onUseQuestion(p)}
                          >
                            Use
                          </button>
                          <CopyButton
                            value={p}
                            label="Copy"
                            copiedLabel="Copied"
                            className="mini-btn ghost"
                          />
                        </div>
                      </div>
                    ))}
                  </div>
                </details>
              ))}
            </div>
          )}
        </section>

        <section id="ask-ai-scope" className="ask-ai-section">
          <div className="ask-ai-section-head">
            <h3>Supported vs. unsupported</h3>
          </div>
          <div className="ask-ai-scope-grid">
            <div className="ask-ai-scope-panel ask-ai-scope-supported">
              <span className="ask-ai-scope-panel-title">
                <AppIcon name="active" size={14} />
                Ask AI can answer
              </span>
              <ul className="ask-ai-scope-list ask-ai-scope-list-yes">
                {SUPPORTED_SCOPE.map((s) => (
                  <li key={s}>{s}</li>
                ))}
              </ul>
            </div>
            <div className="ask-ai-scope-panel ask-ai-scope-unsupported">
              <span className="ask-ai-scope-panel-title">
                <AppIcon name="denied" size={14} />
                Not supported
              </span>
              <ul className="ask-ai-scope-list ask-ai-scope-list-no">
                {UNSUPPORTED_SCOPE.map((s) => (
                  <li key={s}>{s}</li>
                ))}
              </ul>
            </div>
          </div>
        </section>
      </div>
    </DrawerPanel>
  );
}

function resultPath(result: AccessSearchResult): string | null {
  if (!result.deepLinkKey) return null;
  switch (result.deepLinkKind) {
    case "role":
      return appPaths.role(result.applicationId, result.deepLinkKey);
    case "permission":
      return appPaths.permission(result.applicationId, result.deepLinkKey);
    case "policy":
      return appPaths.policy(result.applicationId, result.deepLinkKey);
    case "user":
      return platformPaths.user(result.deepLinkKey);
    case "application":
      return appPaths.dashboard(result.deepLinkKey);
    case "tenant":
      return platformPaths.tenant(result.deepLinkKey);
    case "reviewCampaign":
      // No per-campaign route exists yet — open the app's Certifications list, where the named
      // campaign can be found (client-side selection, not URL-addressable).
      return result.applicationId ? appPaths.certifications(result.applicationId) : null;
    default:
      return null;
  }
}

// The inner content of a citable result line (icon · title/detail/context · open button), rendered
// without its own list-item wrapper so it can be reused inside both a flat <li> (ResultLine) and a
// nested branch node that also carries grandchildren.
function ResultRowInner({
  result,
  variant,
}: {
  result: AccessSearchResult;
  variant?: "child";
}) {
  const navigate = useNavigate();
  const path = resultPath(result);
  // Show ownership chips only for rows that belong to an application — never for a child row
  // (it inherits its parent's context) nor for an application/tenant row (it *is* the context).
  const showContext =
    variant !== "child" &&
    result.entityType !== "APPLICATION" &&
    result.entityType !== "TENANT" &&
    Boolean(
      result.tenantName || result.applicationName || result.applicationId,
    );
  return (
    <>
      <span className="access-search-kind">
        <AppIcon name={iconForKind(result.deepLinkKind)} size={14} />
      </span>
      <span className="access-search-body">
        <span className="access-search-title">{result.title}</span>
        <span className="access-search-detail muted">{result.detail}</span>
        {showContext && (
          <span className="access-search-context">
            {result.tenantName && (
              <span className="access-search-tag" title="Tenant">
                <AppIcon name="tenants" size={11} />
                {result.tenantName}
              </span>
            )}
            {(result.applicationName || result.applicationId) && (
              <span className="access-search-tag" title="Application">
                <AppIcon name="applications" size={11} />
                {result.applicationName || result.applicationId}
              </span>
            )}
          </span>
        )}
      </span>
      {path && (
        <button
          type="button"
          className="access-search-open"
          onClick={() => navigate(path)}
        >
          Open →
        </button>
      )}
    </>
  );
}

// One flat, citable result line — reused for plain records, count samples, and tree children.
function ResultLine({
  result,
  variant,
}: {
  result: AccessSearchResult;
  variant?: "child";
}) {
  return (
    <li
      className={`access-search-item${variant === "child" ? " access-search-child" : ""}`}
    >
      <ResultRowInner result={result} variant={variant} />
    </li>
  );
}

// A tree child, optionally carrying one or more further nested levels (e.g. each permission's
// policies, or — for a three-level chain — each role's permissions and each permission's policies).
// A child with no nested rows renders as a plain line; one with nested rows renders its own row plus
// an indented list beneath it, recursing so any bounded chain depth the backend returns renders
// correctly without the UI needing to know how deep the chain goes.
function ChildBranch({ child }: { child: AccessSearchResult }) {
  const nested = child.children ?? [];
  if (nested.length === 0) {
    return <ResultLine result={child} variant="child" />;
  }
  return (
    <li className="access-search-child-branch">
      <div className="access-search-item access-search-child">
        <ResultRowInner result={child} variant="child" />
      </div>
      <ul className="access-search-children access-search-grandchildren">
        {nested.map((nestedChild, k) => (
          <ChildBranch key={k} child={nestedChild} />
        ))}
      </ul>
    </li>
  );
}

// ── Answer presentation helpers ──────────────────────────────────────────────────────────────

// "reviewCampaign" → "Review campaign", "role" → "Role". Splits camelCase and sentence-cases it so
// entity and field names read naturally in the header.
function humanize(token: string): string {
  const spaced = token
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .toLowerCase()
    .trim();
  return spaced.length === 0 ? spaced : spaced[0].toUpperCase() + spaced.slice(1);
}

// A readable symbol/word for a filter operator, so a resolved filter reads like "Risk level = HIGH".
function operatorLabel(op: string): string {
  switch (op.toLowerCase()) {
    case "eq":
      return "=";
    case "neq":
      return "≠";
    case "contains":
      return "contains";
    case "in":
      return "in";
    case "gt":
      return ">";
    case "gte":
      return "≥";
    case "lt":
      return "<";
    case "lte":
      return "≤";
    default:
      return op;
  }
}

// The leading integer a group detail starts with (e.g. "12 roles" → 12), used to size proportion
// bars. Returns null when the detail has no leading count so the bar is simply omitted.
function leadingCount(detail: string): number | null {
  const match = /^\s*(\d+)/.exec(detail);
  return match ? Number(match[1]) : null;
}

// A one-line summary of how many results came back, phrased per mode.
function resultSummary(mode: string, count: number): string {
  const plural = (word: string) => `${count} ${word}${count === 1 ? "" : "s"}`;
  switch (mode) {
    case "group":
      return plural("group");
    case "count":
      return "total";
    default:
      return plural("result");
  }
}

// A consistent, information-dense header for every answered question: the model's plain-language
// interpretation, the entity and filters it actually resolved to (as chips), and how many results
// came back — so the user can always see *what was asked of the data*, not just the rows. Shown for
// every mode except guidance (which carries its own explanation).
function AnswerHeader({
  answer,
  count,
  mode,
}: {
  answer: AccessSearchResponse;
  count: number;
  mode: string;
}) {
  return (
    <div className="access-search-summary">
      {answer.explanation && (
        <p className="access-search-explain" aria-live="polite">
          <AppIcon name="sparkles" size={14} /> {answer.explanation}
        </p>
      )}
      <div className="access-search-meta">
        {answer.entity && (
          <span className="access-search-entity-badge">{humanize(answer.entity)}</span>
        )}
        {answer.filters.map((f, i) => (
          <span key={i} className="access-search-filter-chip">
            <span className="access-search-filter-field">{humanize(f.field)}</span>
            <span className="access-search-filter-op">{operatorLabel(f.operator)}</span>
            <span className="access-search-filter-value">{f.value}</span>
          </span>
        ))}
        <span className="access-search-result-count">{resultSummary(mode, count)}</span>
      </div>
    </div>
  );
}

export function AccessSearch() {
  const enabled = useAiFeature("accessSearch");
  const search = usePlatformAccessSearch();
  const navigate = useNavigate();
  const [question, setQuestion] = useState("");
  const [answer, setAnswer] = useState<AccessSearchResponse | null>(null);
  const [helpOpen, setHelpOpen] = useState(false);

  if (!enabled) return null;

  async function ask(q: string) {
    const trimmed = q.trim();
    if (trimmed.length === 0) return;
    // Clear any previous answer up front so stale results never linger while
    // the new query runs.
    setAnswer(null);
    try {
      const result = await search.mutateAsync(trimmed);
      setAnswer(result);
    } catch {
      // Genuine transport/timeout errors are surfaced once by the global
      // mutation error handler; swallow here so we don't double-notify.
    }
  }

  const results = answer?.results ?? [];
  const mode = answer?.mode ?? "records";

  function renderAnswer() {
    // The model couldn't confidently answer — offer inferred intent + ready-to-run
    // prompts the engine can actually handle, instead of a dead end.
    if (mode === "guidance" && answer?.guidance) {
      const g = answer.guidance;
      return (
        <div className="access-search-guidance">
          {g.intents.length > 0 && (
            <div className="access-search-guidance-intents">
              <span className="muted">Are you trying to…</span>
              <ul>
                {g.intents.map((intent, i) => (
                  <li key={i}>{intent}?</li>
                ))}
              </ul>
            </div>
          )}
          {g.suggestions.length > 0 && (
            <div className="access-search-guidance-suggestions">
              <span className="muted">Try one of these instead:</span>
              <div className="access-search-suggestions">
                {g.suggestions.map((s) => (
                  <button
                    key={s}
                    type="button"
                    className="access-search-chip"
                    onClick={() => {
                      setQuestion(s);
                      void ask(s);
                    }}
                  >
                    {s}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      );
    }

    if (results.length === 0) {
      return (
        <p className="access-search-empty muted">
          No matching access found. Try rephrasing your question.
        </p>
      );
    }

    // A single scalar count — show it as a prominent metric, then list the matching records
    // behind it (a bounded, citable sample) so the number is auditable rather than a black box.
    if (mode === "count") {
      const total = Number(results[0].title);
      const sample = results[0].children ?? [];
      return (
        <div className="access-search-count">
          <div className="access-search-metric">
            <span className="access-search-metric-value">{results[0].title}</span>
            <span className="access-search-metric-label">{results[0].detail}</span>
          </div>
          {sample.length > 0 && (
            <>
              <p className="access-search-sample-note muted">
                {Number.isFinite(total) && total > sample.length
                  ? `Showing first ${sample.length} of ${total}`
                  : "Matching records"}
              </p>
              <ul className="access-search-results">
                {sample.map((result, i) => (
                  <ResultLine key={i} result={result} />
                ))}
              </ul>
            </>
          )}
        </div>
      );
    }

    // Per-group counts — a ranked list with a count pill and a proportion bar. When a group's rows
    // carry their own children it is a two-level cross-tab (e.g. roles by risk level then status):
    // each primary bucket is rendered with its nested secondary buckets beneath it.
    if (mode === "group") {
      const nested = results.some((r) => (r.children?.length ?? 0) > 0);
      const maxOuter = Math.max(1, ...results.map((r) => leadingCount(r.detail) ?? 0));
      return (
        <ul className="access-search-results access-search-group">
          {results.map((r, i) => {
            const path = resultPath(r);
            const outerCount = leadingCount(r.detail);
            const kids = r.children ?? [];
            const maxInner = Math.max(1, ...kids.map((k) => leadingCount(k.detail) ?? 0));
            return (
              <li key={i} className="access-search-group-node">
                <div className="access-search-item access-search-group-row">
                  <span className="access-search-kind">
                    <AppIcon name={iconForKind(r.deepLinkKind)} size={14} />
                  </span>
                  <span className="access-search-body">
                    <span className="access-search-title">{r.title}</span>
                    {outerCount !== null && (
                      <span className="access-search-proportion" aria-hidden="true">
                        <span
                          className="access-search-proportion-fill"
                          style={{ width: `${Math.round((outerCount / maxOuter) * 100)}%` }}
                        />
                      </span>
                    )}
                  </span>
                  <span className="access-search-count-pill">{r.detail}</span>
                  {path && (
                    <button
                      type="button"
                      className="access-search-open"
                      onClick={() => navigate(path)}
                    >
                      Open →
                    </button>
                  )}
                </div>
                {nested && kids.length > 0 && (
                  <ul className="access-search-subgroups">
                    {kids.map((k, j) => {
                      const innerCount = leadingCount(k.detail);
                      return (
                        <li key={j} className="access-search-item access-search-subgroup-row">
                          <span className="access-search-body">
                            <span className="access-search-subgroup-title">{k.title}</span>
                            {innerCount !== null && (
                              <span className="access-search-proportion access-search-proportion-sm" aria-hidden="true">
                                <span
                                  className="access-search-proportion-fill"
                                  style={{ width: `${Math.round((innerCount / maxInner) * 100)}%` }}
                                />
                              </span>
                            )}
                          </span>
                          <span className="access-search-count-pill access-search-count-pill-sm">
                            {k.detail}
                          </span>
                        </li>
                      );
                    })}
                  </ul>
                )}
              </li>
            );
          })}
        </ul>
      );
    }

    // Parent → children tree (relationship include).
    if (mode === "tree") {
      return (
        <ul className="access-search-tree">
          {results.map((parent, i) => {
            const path = resultPath(parent);
            const kids = parent.children ?? [];
            return (
              <li key={i} className="access-search-tree-node">
                <div className="access-search-item access-search-parent">
                  <span className="access-search-kind">
                    <AppIcon name={iconForKind(parent.deepLinkKind)} size={14} />
                  </span>
                  <span className="access-search-body">
                    <span className="access-search-title">{parent.title}</span>
                    <span className="access-search-detail muted">
                      {parent.detail}
                      {` · ${kids.length} related`}
                    </span>
                  </span>
                  {path && (
                    <button
                      type="button"
                      className="access-search-open"
                      onClick={() => navigate(path)}
                    >
                      Open →
                    </button>
                  )}
                </div>
                {kids.length > 0 ? (
                  <ul className="access-search-children">
                    {kids.map((child, j) => (
                      <ChildBranch key={j} child={child} />
                    ))}
                  </ul>
                ) : (
                  <p className="access-search-children-empty muted">
                    No related records.
                  </p>
                )}
              </li>
            );
          })}
        </ul>
      );
    }

    // Plain records.
    return (
      <ul className="access-search-results">
        {results.map((result, i) => (
          <ResultLine key={i} result={result} />
        ))}
      </ul>
    );
  }

  return (
    <section className="access-search" aria-label="Natural-language access search">
      <div className="access-search-toolbar">
        <form
          className="access-search-bar"
          onSubmit={(e) => {
            e.preventDefault();
            void ask(question);
          }}
        >
          <AppIcon name="sparkles" size={16} />
          <input
            type="search"
            className="access-search-input"
            placeholder="Ask across every application — e.g. which roles are privileged?"
            aria-label="Ask a natural-language access question"
            value={question}
            onChange={(e) => setQuestion(e.target.value)}
          />
          <button
            type="submit"
            className="mini-btn is-active"
            disabled={search.isPending || question.trim().length === 0}
          >
            {search.isPending ? "Searching…" : "Ask AI"}
          </button>
        </form>
        <button
          type="button"
          className="access-search-help-trigger"
          onClick={() => setHelpOpen(true)}
        >
          <AppIcon name="info" size={14} />
          About Ask AI
        </button>
      </div>

      {!answer && !search.isPending && (
        <div className="access-search-suggestions">
          {SUGGESTIONS.map((s) => (
            <button
              key={s}
              type="button"
              className="access-search-chip"
              onClick={() => {
                setQuestion(s);
                void ask(s);
              }}
            >
              {s}
            </button>
          ))}
        </div>
      )}

      {search.isPending ? (
        <Spinner label="Interpreting your question…" />
      ) : answer ? (
        <div className="access-search-answer">
          {mode !== "guidance" && (
            <AnswerHeader answer={answer} count={results.length} mode={mode} />
          )}
          {renderAnswer()}
        </div>
      ) : null}

      <AskAiHelpDrawer
        open={helpOpen}
        onClose={() => setHelpOpen(false)}
        onUseQuestion={(q) => {
          setHelpOpen(false);
          setQuestion(q);
          void ask(q);
        }}
      />
    </section>
  );
}

function iconForKind(kind: AccessSearchResult["deepLinkKind"]) {
  switch (kind) {
    case "role":
      return "roles" as const;
    case "permission":
      return "permissions" as const;
    case "policy":
      return "policies" as const;
    case "user":
      return "users" as const;
    case "application":
      return "applications" as const;
    case "tenant":
      return "tenants" as const;
    case "reviewCampaign":
      return "audit" as const;
    default:
      return "search" as const;
  }
}
