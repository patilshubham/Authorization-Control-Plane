import { useEffect, useMemo, useRef, useState } from "react";
import { useCallbackRef } from "./useCallbackRef";
import { AppIcon } from "../../components/icons";
import { SlideOver } from "../../components/primitives";
import { useAiFeature } from "../../api/aiConfig";
import { usePolicyDraft } from "../../api/hooks";
import { useToast } from "../../components/Toast";
import { userFacingError } from "../../apiClient";
import {
  OPERATORS,
  OPERATOR_BY_ID,
  type Combinator,
  type ConditionNode,
  type GroupNode,
  type OperatorId,
  type OperatorMeta,
  type RuleNode,
  type ValueMode,
  composeValue,
  countRules,
  deserialize,
  duplicateInto,
  emptyRoot,
  insertChild,
  newGroup,
  newRule,
  operatorArity,
  removeNode,
  reorderChild,
  serialize,
  summarize,
  updateNode,
  validate,
  valueKey,
  valueMode,
} from "./model";

// Key suggestions for the attribute field (the namespace is chosen separately).
// These are illustrative only — context/assignment keys are defined by your app.
const DEFAULT_ATTRIBUTE_SUGGESTIONS = [
  "amount",
  "currency",
  "region",
  "vendorRisk",
  "environment",
  "tenantId",
  "approvalLimit",
  "department",
];

// The fixed set of server-computed "system.*" attributes the engine exposes
// (see EfAuthorizationPolicyEngine.BuildSystemContext): the evaluation clock.
const SYSTEM_KEYS = ["now", "date", "time", "hour", "dayOfWeek", "dow"];

const DEFAULT_REFERENCE_SUGGESTIONS = [
  "amount",
  "approvalLimit",
  "region",
  "department",
  "tenantId",
];

// Starter prompts shown as clickable chips before the author types anything.
// Chosen to showcase the range of supported conditions (amounts, lists, dates,
// day-of-week, and negation) so the copilot's capabilities are discoverable.
const AI_EXAMPLE_PROMPTS = [
  "Allow when amount is under 5000 and region is EU",
  "Deny outside business hours (before 09:00 or after 17:00)",
  "Allow only on weekdays (day of week Monday to Friday)",
  "Deny when the vendor is in the blocked list",
];

export type ConditionBuilderProps = {
  value: string;
  onChange: (json: string) => void;
  onValidityChange?: (valid: boolean) => void;
  /**
   * Keys of the application's stored reference-data documents. When a value uses the
   * <c>reference.</c> source these are offered as autocomplete suggestions.
   */
  referenceDataKeys?: string[];
  /**
   * When provided together with the AI policy-authoring feature being enabled,
   * shows the natural-language "draft with AI" panel. The generated draft is a
   * suggestion the author reviews and edits before saving.
   */
  applicationId?: string;
  /**
   * Called when the AI draft infers the policy effect (ALLOW/DENY) from the
   * instruction, so the parent can pre-select its effect control. The author
   * still confirms the effect before saving.
   */
  onAiEffectSuggested?: (effect: "ALLOW" | "DENY") => void;
};

/**
 * Visual, no-JSON policy condition builder. Emits backend-contract JSON on every
 * change. Supports nested AND/OR groups, type-aware value inputs, references vs.
 * literals, drag reordering, live plain-English preview, validation, and an
 * optional advanced JSON view with two-way sync (import/export).
 */
export function ConditionBuilder({
  value,
  onChange,
  onValidityChange,
  referenceDataKeys,
  applicationId,
  onAiEffectSuggested,
}: ConditionBuilderProps) {
  const [root, setRoot] = useState<GroupNode>(() => deserialize(value).root);
  const [jsonMode, setJsonMode] = useState(false);
  const [jsonDraft, setJsonDraft] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const onChangeRef = useCallbackRef(onChange);
  const onValidityChangeRef = useCallbackRef(onValidityChange ?? (() => {}));

  const aiPolicyAuthoring = useAiFeature("policyAuthoring");
  const aiEnabled = aiPolicyAuthoring && !!applicationId;

  const attrList = DEFAULT_ATTRIBUTE_SUGGESTIONS;
  const refList = DEFAULT_REFERENCE_SUGGESTIONS;
  const refDataList = referenceDataKeys ?? [];

  const json = useMemo(() => serialize(root), [root]);
  const issues = useMemo(() => validate(root), [root]);
  const summary = useMemo(() => summarize(root), [root]);
  const issuesById = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const issue of issues) {
      const list = map.get(issue.nodeId) ?? [];
      list.push(issue.message);
      map.set(issue.nodeId, list);
    }
    return map;
  }, [issues]);

  // Emit backend JSON whenever the tree changes.
  useEffect(() => {
    onChangeRef(json);
  }, [json, onChangeRef]);

  // Report whether the current condition tree (and JSON view) is valid.
  useEffect(() => {
    onValidityChangeRef(issues.length === 0 && !jsonError);
  }, [issues.length, jsonError, onValidityChangeRef]);

  const listId = useRef(
    `attr-${Math.random().toString(36).slice(2, 8)}`,
  ).current;

  function edit(id: string, patch: (n: ConditionNode) => ConditionNode) {
    setRoot((r) => updateNode(r, id, patch));
  }

  function enterJsonMode() {
    setJsonDraft(json);
    setJsonError(null);
    setJsonMode(true);
  }

  function applyJson(next: string) {
    setJsonDraft(next);
    const result = deserialize(next);
    if (result.error) {
      setJsonError(result.error);
      return;
    }
    setJsonError(null);
    setRoot(result.root);
  }

  async function copyJson() {
    try {
      await navigator.clipboard.writeText(json);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div className="cond-builder" role="group" aria-label="Condition builder">
      {aiEnabled && applicationId && (
        <AiPolicyDraftPanel
          applicationId={applicationId}
          onApply={(nextJson, suggestedEffect) => {
            const result = deserialize(nextJson);
            if (!result.error) {
              setRoot(result.root);
              if (jsonMode) {
                setJsonDraft(nextJson);
                setJsonError(null);
              }
            }
            if (suggestedEffect) {
              onAiEffectSuggested?.(suggestedEffect);
            }
          }}
        />
      )}
      <div className="cond-toolbar">
        <div className="cond-toolbar-info">
          <span className="cond-count">
            {countRules(root)} condition{countRules(root) === 1 ? "" : "s"}
          </span>
          {issues.length > 0 && (
            <span className="cond-invalid" role="status">
              {issues.length} to fix
            </span>
          )}
        </div>
        <div className="cond-toolbar-actions">
          <button
            type="button"
            className="mini-btn cond-help-btn"
            onClick={() => setHelpOpen(true)}
            title="Learn what every field and operator does"
          >
            Get help with conditions
          </button>
          <button
            type="button"
            className="mini-btn"
            onClick={copyJson}
            title="Copy the generated JSON"
          >
            {copied ? "Copied" : "Copy JSON"}
          </button>
          <button
            type="button"
            className={`mini-btn ${jsonMode ? "is-active" : ""}`}
            aria-pressed={jsonMode}
            onClick={() => (jsonMode ? setJsonMode(false) : enterJsonMode())}
          >
            {jsonMode ? "Visual editor" : "Edit as JSON"}
          </button>
        </div>
      </div>

      {jsonMode ? (
        <div className="cond-json-edit">
          <label className="cond-json-label" htmlFor={`${listId}-json`}>
            Advanced — paste or edit JSON. Changes sync back to the visual
            editor.
          </label>
          <textarea
            id={`${listId}-json`}
            className={`cond-json-area ${jsonError ? "has-error" : ""}`}
            rows={10}
            spellCheck={false}
            value={jsonDraft}
            onChange={(e) => applyJson(e.target.value)}
          />
          {jsonError ? (
            <p className="cond-json-error">{jsonError}</p>
          ) : (
            <p className="cond-json-ok">Valid — applied to the builder.</p>
          )}
        </div>
      ) : (
        <GroupEditor
          node={root}
          isRoot
          depth={0}
          issuesById={issuesById}
          attrList={attrList}
          refList={refList}
          refDataList={refDataList}
          onEdit={edit}
          onAddRule={(groupId) =>
            setRoot((r) => insertChild(r, groupId, newRule()))
          }
          onAddGroup={(groupId) =>
            setRoot((r) => insertChild(r, groupId, newGroup("any")))
          }
          onRemove={(id) => setRoot((r) => removeNode(r, id))}
          onDuplicate={(groupId, id) =>
            setRoot((r) => duplicateInto(r, groupId, id))
          }
          onReorder={(groupId, from, to) =>
            setRoot((r) => reorderChild(r, groupId, from, to))
          }
          onReset={() => setRoot(emptyRoot())}
        />
      )}

      <ConditionsHelpDrawer open={helpOpen} onClose={() => setHelpOpen(false)} />

      <div className="cond-preview" aria-live="polite">
        <span className="cond-preview-label">Reads as</span>
        <p className="cond-preview-text">{summary}</p>
      </div>
    </div>
  );
}

const HELP_OPERATOR_GROUPS: { id: OperatorMeta["group"]; label: string }[] = [
  { id: "compare", label: "Comparison" },
  { id: "text", label: "Text" },
  { id: "set", label: "Set / list" },
  { id: "datetime", label: "Date & range" },
  { id: "presence", label: "Presence (needs no value)" },
];

/**
 * Full reference for the condition builder, opened from the toolbar. Explains
 * every field (attribute, operator, value source, value), lists all operators
 * with examples, and clarifies how the fields relate.
 */
function ConditionsHelpDrawer({
  open,
  onClose,
}: {
  open: boolean;
  onClose: () => void;
}) {
  return (
    <SlideOver open={open} title="How conditions work" onClose={onClose} wide>
      <div className="cond-help-doc">
        <section>
          <h3>The idea</h3>
          <p>
            A policy&rsquo;s conditions are a tree of rules, grouped by{" "}
            <b>All</b> (AND), <b>Any</b> (OR) or <b>None</b> (NOT). Each rule
            reads left&nbsp;to&nbsp;right: <em>attribute → operator → value</em>.
            The rule is true when the attribute compares to the value the way the
            operator says. The engine evaluates this for every one of the
            subject&rsquo;s matching assignments.
          </p>
        </section>

        <section>
          <h3>Anatomy of one rule</h3>
          <div className="cha-diagram" aria-hidden="true">
            <div className="cha-node">
              <span className="cha-num">1</span>
              <span className="cha-title">Attribute</span>
              <span className="cha-mock">
                <span className="cha-sel">Request context ▾</span>
                <span className="cha-inp">amount</span>
              </span>
              <span className="cha-cap">what you test</span>
            </div>
            <span className="cha-arrow">→</span>
            <div className="cha-node">
              <span className="cha-num">2</span>
              <span className="cha-title">Operator</span>
              <span className="cha-mock">
                <span className="cha-sel">greater than ▾</span>
              </span>
              <span className="cha-cap">how to compare</span>
            </div>
            <span className="cha-arrow">→</span>
            <div className="cha-node">
              <span className="cha-num">3</span>
              <span className="cha-title">Compare to</span>
              <span className="cha-mock">
                <span className="cha-sel">Literal value ▾</span>
                <span className="cha-inp">5000</span>
              </span>
              <span className="cha-cap">what to check against</span>
            </div>
          </div>
          <p className="cond-help-tip">
            Reads as <b>&ldquo;amount is greater than 5000&rdquo;</b>. The{" "}
            <b>Reads as</b> line under the builder always shows this plain-English
            translation — use it to sanity-check your rule.
          </p>
        </section>

        <section>
          <h3>1 · Attribute — what you test</h3>
          <p>
            Choose a <b>source</b>, then a <b>key</b>. Only these three sources
            are valid on the left side:
          </p>
          <ul>
            <li>
              <b>Request context</b> (<code>context.</code>) — data the calling
              application sends with each authorization check. Keys are
              app-defined, e.g. <code>context.amount</code>,{" "}
              <code>context.region</code>.
            </li>
            <li>
              <b>Assignment attribute</b> (<code>assignment.</code>) — ABAC
              attributes stored on the subject&rsquo;s grant, e.g.{" "}
              <code>assignment.approvalLimit</code>.
            </li>
            <li>
              <b>System value</b> (<code>system.</code>) — the server&rsquo;s
              evaluation clock. Fixed keys: <code>now</code>, <code>date</code>,{" "}
              <code>time</code>, <code>hour</code>, <code>dayOfWeek</code>,{" "}
              <code>dow</code> (1=Mon … 7=Sun).
            </li>
          </ul>
          <p className="cond-help-tip">
            The key suggestions are examples only — type whatever attributes your
            application actually sends.
          </p>
        </section>

        <section>
          <h3>2 · Operator — how you compare</h3>
          {HELP_OPERATOR_GROUPS.map((group) => (
            <div key={group.id} className="cond-help-ops">
              <h4>{group.label}</h4>
              <ul>
                {OPERATORS.filter((o) => o.group === group.id).map((o) => (
                  <li key={o.id}>
                    <span className="cond-help-op">
                      <code>{o.symbol}</code> {o.label}
                    </span>
                    <span className="cond-help-op-hint">{o.hint}</span>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </section>

        <section>
          <h3>3 · Value source — what you compare against</h3>
          <p>
            The right side is <b>independent</b> of the attribute — that is why it
            offers more choices than the attribute source:
          </p>
          <ul>
            <li>
              <b>Literal value</b> — a fixed value you type, e.g. <code>5000</code>{" "}
              or <code>EU</code>.
            </li>
            <li>
              <b>Request context / Assignment attribute / System value</b> —
              compare against another live attribute instead of a fixed value,
              e.g. <code>context.amount ≤ assignment.approvalLimit</code>.
            </li>
            <li>
              <b>Reference data</b> (<code>reference.</code>) — compare against a
              shared list maintained for this application, e.g.{" "}
              <code>context.region is any of reference.approved-markets</code>.
              (Reference data is valid only here, never as the attribute.)
            </li>
          </ul>
        </section>

        <section>
          <h3>4 · Value — the input adapts</h3>
          <ul>
            <li>Most operators take a single value.</li>
            <li>
              <b>between</b> shows two bounds (min → max).
            </li>
            <li>
              <b>is any of</b>, <b>contains all of</b>, … take a comma-separated
              list.
            </li>
            <li>
              <b>is present</b>, <b>is true</b>, … need no value at all.
            </li>
            <li>
              With the <b>System value</b> source, you pick from the fixed system
              keys instead of typing.
            </li>
          </ul>
        </section>

        <section>
          <h3>Do the dropdowns affect each other?</h3>
          <ul>
            <li>
              <b>Operator → Value:</b> yes — the operator decides the value&rsquo;s
              shape (single, list, range, or none).
            </li>
            <li>
              <b>Attribute source → key:</b> yes — choosing <b>System value</b>{" "}
              turns the key into a fixed dropdown.
            </li>
            <li>
              <b>Attribute source ↔ Value source:</b> no, by design — the left is
              the attribute under test and the right is what it is compared to, so
              both independently offer namespaces.
            </li>
          </ul>
        </section>

        <section>
          <h3>Recipes — what to pick in each dropdown</h3>
          <p>Common goals and the exact selections that build them:</p>
          <table className="cond-help-table">
            <thead>
              <tr>
                <th>Goal</th>
                <th>Attribute</th>
                <th>Operator</th>
                <th>Compare to</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Only amounts over 5,000</td>
                <td>Request context · <code>amount</code></td>
                <td>greater than</td>
                <td>Literal value · <code>5000</code></td>
              </tr>
              <tr>
                <td>Within the user&rsquo;s own approval limit</td>
                <td>Request context · <code>amount</code></td>
                <td>less than or equal</td>
                <td>Assignment attribute · <code>approvalLimit</code></td>
              </tr>
              <tr>
                <td>Weekends only</td>
                <td>System value · <code>dayOfWeek</code></td>
                <td>is any of</td>
                <td>Literal value · <code>Saturday, Sunday</code></td>
              </tr>
              <tr>
                <td>Business hours (09:00–17:00)</td>
                <td>System value · <code>hour</code></td>
                <td>between (inclusive)</td>
                <td>Literal value · <code>9, 17</code></td>
              </tr>
              <tr>
                <td>Region must be approved</td>
                <td>Request context · <code>region</code></td>
                <td>is any of</td>
                <td>Reference data · <code>approved-markets</code></td>
              </tr>
              <tr>
                <td>A flag must be set</td>
                <td>Request context · <code>mfaVerified</code></td>
                <td>is true</td>
                <td><em>no value needed</em></td>
              </tr>
            </tbody>
          </table>
        </section>

        <section>
          <h3>Tips</h3>
          <ul>
            <li>
              Combine rules with <b>All</b> / <b>Any</b> / <b>None</b> groups, and
              nest groups for logic like &ldquo;A and (B or C)&rdquo;.
            </li>
            <li>
              A <b>DENY</b> policy wins over an <b>ALLOW</b> when both match — keep
              deny rules tightly scoped.
            </li>
            <li>
              If a referenced <code>context.</code> key is missing at runtime the
              rule fails closed (does not match), so make sure your app sends every
              key you reference.
            </li>
            <li>
              Prefer <b>Assignment attribute</b> or <b>Reference data</b> over
              hard-coded literals — you can change limits and lists without editing
              every policy.
            </li>
            <li>
              Use <b>Edit as JSON</b> for bulk changes, then switch back to the
              visual editor — they stay in sync.
            </li>
          </ul>
        </section>
      </div>
    </SlideOver>
  );
}

type AiPolicyDraftPanelProps = {
  applicationId: string;
  onApply: (conditionsJson: string, suggestedEffect: "ALLOW" | "DENY" | null) => void;
};

/**
 * F1 — Policy Authoring Copilot. Turns a plain-language instruction into a draft
 * condition tree. The output is advisory: it is loaded into the visual editor for
 * the author to review, adjust and save. Only mounted when the AI feature is on.
 */
function AiPolicyDraftPanel({ applicationId, onApply }: AiPolicyDraftPanelProps) {
  const [instruction, setInstruction] = useState("");
  const draft = usePolicyDraft();
  const toast = useToast();

  async function generate() {
    const text = instruction.trim();
    if (text.length === 0) return;
    try {
      const result = await draft.mutateAsync({ applicationId, instruction: text });
      onApply(result.conditionsJson, result.suggestedEffect);
      if (result.warnings.length > 0) {
        toast.info(result.warnings[0]);
      } else if (result.suggestedEffect === "DENY") {
        toast.info("Drafted as a DENY policy — confirm the effect and conditions before saving.");
      } else {
        toast.success("Draft applied. Review the effect and conditions before saving.");
      }
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  return (
    <div className="cond-ai" role="group" aria-label="Draft policy with AI">
      <label className="cond-ai-label" htmlFor="cond-ai-instruction">
        <AppIcon name="sparkles" size={14} /> Describe the rule in plain language
      </label>
      <div className="cond-ai-row">
        <textarea
          id="cond-ai-instruction"
          className="cond-ai-input"
          rows={2}
          placeholder="e.g. Allow when the request amount is under 5000 and the region is EU"
          value={instruction}
          disabled={draft.isPending}
          onChange={(e) => setInstruction(e.target.value)}
        />
        <button
          type="button"
          className="mini-btn is-active"
          disabled={draft.isPending || instruction.trim().length === 0}
          onClick={generate}
        >
          <AppIcon name="sparkles" size={14} />{" "}
          {draft.isPending ? "Drafting…" : "Draft with AI"}
        </button>
      </div>
      {instruction.trim().length === 0 && (
        <div className="cond-ai-examples" aria-label="Example prompts">
          {AI_EXAMPLE_PROMPTS.map((example) => (
            <button
              key={example}
              type="button"
              className="cond-ai-chip"
              disabled={draft.isPending}
              onClick={() => setInstruction(example)}
            >
              {example}
            </button>
          ))}
        </div>
      )}
      <p className="cond-ai-hint">
        AI drafts are suggestions only — always review before saving.
      </p>
    </div>
  );
}

type GroupEditorProps = {
  node: GroupNode;
  isRoot: boolean;
  depth: number;
  issuesById: Map<string, string[]>;
  attrList: string[];
  refList: string[];
  refDataList: string[];
  onEdit: (id: string, patch: (n: ConditionNode) => ConditionNode) => void;
  onAddRule: (groupId: string) => void;
  onAddGroup: (groupId: string) => void;
  onRemove: (id: string) => void;
  onDuplicate: (groupId: string, id: string) => void;
  onReorder: (groupId: string, from: number, to: number) => void;
  onReset: () => void;
};

function GroupEditor(props: GroupEditorProps) {
  const {
    node,
    isRoot,
    depth,
    issuesById,
    onEdit,
    onAddRule,
    onAddGroup,
    onRemove,
    onDuplicate,
    onReorder,
    onReset,
  } = props;
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const [overIndex, setOverIndex] = useState<number | null>(null);
  const groupIssues = issuesById.get(node.id) ?? [];

  function setCombinator(c: Combinator) {
    onEdit(node.id, (n) => (n.kind === "group" ? { ...n, combinator: c } : n));
  }

  return (
    <div
      className={`cond-group ${depth > 0 ? "is-nested" : "is-root"} combinator-${node.combinator}`}
      data-depth={depth}
    >
      <div className="cond-group-head">
        <div
          className="cond-combinator"
          role="radiogroup"
          aria-label="Match type"
        >
          <button
            type="button"
            className={node.combinator === "all" ? "is-active" : ""}
            aria-pressed={node.combinator === "all"}
            onClick={() => setCombinator("all")}
          >
            ALL
          </button>
          <button
            type="button"
            className={node.combinator === "any" ? "is-active" : ""}
            aria-pressed={node.combinator === "any"}
            onClick={() => setCombinator("any")}
          >
            ANY
          </button>
          <button
            type="button"
            className={node.combinator === "none" ? "is-active" : ""}
            aria-pressed={node.combinator === "none"}
            onClick={() => setCombinator("none")}
          >
            NONE
          </button>
        </div>
        <span className="cond-combinator-hint">
          {node.combinator === "all"
            ? "All of the following must be true"
            : node.combinator === "any"
              ? "Any one of the following can be true"
              : "None of the following may be true (NOT)"}
        </span>
        <div className="cond-group-tools">
          {!isRoot && (
            <button
              type="button"
              className="icon-btn danger"
              title="Remove group"
              aria-label="Remove group"
              onClick={() => onRemove(node.id)}
            >
              ✕
            </button>
          )}
        </div>
      </div>

      {groupIssues.length > 0 && (
        <p className="cond-row-error">{groupIssues.join(" ")}</p>
      )}

      <div className="cond-children">
        {node.children.length === 0 && (
          <p className="cond-empty">No conditions yet — add one below.</p>
        )}
        {node.children.map((child, index) => {
          const isDragging = dragIndex === index;
          const isOver =
            overIndex === index && dragIndex !== null && dragIndex !== index;
          return (
            <div
              key={child.id}
              className={`cond-child ${isDragging ? "is-dragging" : ""} ${isOver ? "is-over" : ""}`}
              draggable={child.kind === "rule"}
              onDragStart={() => setDragIndex(index)}
              onDragEnter={() => setOverIndex(index)}
              onDragOver={(e) => {
                if (dragIndex !== null) e.preventDefault();
              }}
              onDrop={() => {
                if (dragIndex !== null && dragIndex !== index)
                  onReorder(node.id, dragIndex, index);
                setDragIndex(null);
                setOverIndex(null);
              }}
              onDragEnd={() => {
                setDragIndex(null);
                setOverIndex(null);
              }}
            >
              {index > 0 && (
                <span className="cond-joiner">
                  {node.combinator === "all" ? "AND" : "OR"}
                </span>
              )}
              {child.kind === "rule" ? (
                <RuleEditor
                  node={child}
                  issues={issuesById.get(child.id) ?? []}
                  attrList={props.attrList}
                  refList={props.refList}
                  refDataList={props.refDataList}
                  onEdit={onEdit}
                  onRemove={() => onRemove(child.id)}
                  onDuplicate={() => onDuplicate(node.id, child.id)}
                />
              ) : (
                <GroupEditor
                  {...props}
                  node={child}
                  isRoot={false}
                  depth={depth + 1}
                />
              )}
            </div>
          );
        })}
      </div>

      <div className="cond-group-actions">
        <button
          type="button"
          className="mini-btn"
          onClick={() => onAddRule(node.id)}
        >
          + Condition
        </button>
        <button
          type="button"
          className="mini-btn"
          onClick={() => onAddGroup(node.id)}
        >
          + Group
        </button>
        {isRoot && node.children.length > 0 && (
          <button type="button" className="mini-btn ghost" onClick={onReset}>
            Clear all
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * App-styled autocomplete used for attribute keys and reference values. Replaces
 * the native <datalist>, whose popup cannot be styled and rendered as an
 * oversized, mispositioned OS box. Shows a filtered, keyboard-navigable list of
 * suggestions while still allowing any free-text value.
 */
function SuggestInput({
  value,
  onChange,
  suggestions,
  placeholder,
  ariaLabel,
  className,
  type = "text",
  inputMode,
}: {
  value: string;
  onChange: (value: string) => void;
  suggestions: string[];
  placeholder?: string;
  ariaLabel: string;
  className?: string;
  type?: "text" | "number";
  inputMode?: "decimal";
}) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const wrapRef = useRef<HTMLDivElement>(null);

  const matches = useMemo(() => {
    const query = value.trim().toLowerCase();
    return suggestions
      .filter(
        (s) => !query || (s.toLowerCase().includes(query) && s.toLowerCase() !== query),
      )
      .slice(0, 8);
  }, [suggestions, value]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  const showList = open && matches.length > 0;

  function choose(v: string) {
    onChange(v);
    setOpen(false);
    setActive(-1);
  }

  return (
    <div className={`cond-suggest ${className ?? ""}`} ref={wrapRef}>
      <input
        className="cond-suggest-input"
        type={type}
        inputMode={inputMode}
        value={value}
        placeholder={placeholder}
        aria-label={ariaLabel}
        role="combobox"
        aria-expanded={showList}
        aria-autocomplete="list"
        autoComplete="off"
        onChange={(e) => {
          onChange(e.target.value);
          setOpen(true);
          setActive(-1);
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={(e) => {
          if (e.key === "ArrowDown") {
            e.preventDefault();
            setOpen(true);
            setActive((i) => Math.min(i + 1, matches.length - 1));
          } else if (e.key === "ArrowUp") {
            e.preventDefault();
            setActive((i) => Math.max(i - 1, 0));
          } else if (e.key === "Enter" && showList && active >= 0) {
            e.preventDefault();
            choose(matches[active]);
          } else if (e.key === "Escape") {
            setOpen(false);
          }
        }}
      />
      {showList && (
        <ul className="cond-suggest-list" role="listbox">
          {matches.map((s, i) => (
            <li key={s}>
              <button
                type="button"
                role="option"
                aria-selected={i === active}
                className={`cond-suggest-opt ${i === active ? "is-active" : ""}`}
                onMouseDown={(e) => {
                  e.preventDefault();
                  choose(s);
                }}
              >
                {s}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function RuleEditor({
  node,
  issues,
  attrList,
  refList,
  refDataList,
  onEdit,
  onRemove,
  onDuplicate,
}: {
  node: RuleNode;
  issues: string[];
  attrList: string[];
  refList: string[];
  refDataList: string[];
  onEdit: (id: string, patch: (n: ConditionNode) => ConditionNode) => void;
  onRemove: () => void;
  onDuplicate: () => void;
}) {
  const arity = operatorArity(node.operator);
  const mode = valueMode(node.value);
  const key = valueKey(node.value);
  const opMeta = OPERATOR_BY_ID[node.operator];
  const hasError = issues.length > 0;

  function setAttribute(attribute: string) {
    onEdit(node.id, (n) => (n.kind === "rule" ? { ...n, attribute } : n));
  }
  // The attribute (left side) is always a namespaced reference — the engine only
  // resolves context. / assignment. / system. here (never a literal or reference.).
  // Edit it as a namespace + key so the control matches the operator/value selects
  // and the key input can adapt (a fixed dropdown for the system clock).
  const attrNamespace: "context" | "assignment" | "system" =
    node.attribute.startsWith("assignment.")
      ? "assignment"
      : node.attribute.startsWith("system.")
        ? "system"
        : "context";
  const attrKey = node.attribute.startsWith(`${attrNamespace}.`)
    ? node.attribute.slice(attrNamespace.length + 1)
    : node.attribute;
  function setAttrNamespace(ns: "context" | "assignment" | "system") {
    setAttribute(`${ns}.${ns === "system" ? "" : attrKey}`);
  }
  function setAttrKey(nextKey: string) {
    setAttribute(`${attrNamespace}.${nextKey}`);
  }
  function setOperator(operator: OperatorId) {
    onEdit(node.id, (n) => {
      if (n.kind !== "rule") return n;
      const nextArity = operatorArity(operator);
      // Clear value when switching to a presence operator (no value needed).
      const value = nextArity === "none" ? "" : n.value;
      return { ...n, operator, value };
    });
  }
  function setValueMode(nextMode: ValueMode) {
    onEdit(node.id, (n) =>
      n.kind === "rule"
        ? { ...n, value: composeValue(nextMode, valueKey(n.value)) }
        : n,
    );
  }
  function setValueKey(nextKey: string) {
    onEdit(node.id, (n) =>
      n.kind === "rule"
        ? { ...n, value: composeValue(valueMode(n.value), nextKey) }
        : n,
    );
  }

  return (
    <div className={`cond-rule ${hasError ? "has-error" : ""}`}>
      <span className="cond-drag" title="Drag to reorder" aria-hidden="true">
        ⋮⋮
      </span>

      <div className="cond-fields">
      <div className="cond-field cond-field-attr">
        <span className="cond-field-label">Attribute</span>
        <div className="cond-attr-group">
        <select
          className="cond-attr-ns"
          value={attrNamespace}
          aria-label="Attribute source"
          title="What kind of value you're testing"
          onChange={(e) =>
            setAttrNamespace(
              e.target.value as "context" | "assignment" | "system",
            )
          }
        >
          <option value="context">Request context</option>
          <option value="assignment">Assignment attribute</option>
          <option value="system">System value</option>
        </select>
        {attrNamespace === "system" ? (
          <select
            className="cond-attr-key"
            value={attrKey}
            aria-label="System attribute"
            onChange={(e) => setAttrKey(e.target.value)}
          >
            <option value="">Choose…</option>
            {SYSTEM_KEYS.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </select>
        ) : (
          <SuggestInput
            className="cond-attr-key"
            value={attrKey}
            suggestions={attrList}
            placeholder={
              attrNamespace === "assignment" ? "approvalLimit" : "amount"
            }
            ariaLabel="Attribute key"
            onChange={setAttrKey}
          />
        )}
        </div>
      </div>

      <span className="cond-connector" aria-hidden="true">→</span>

      <div className="cond-field cond-field-op">
        <span className="cond-field-label">Operator</span>
        <select
        className="cond-op"
        value={node.operator}
        aria-label="Operator"
        title={opMeta?.hint}
        onChange={(e) => setOperator(e.target.value as OperatorId)}
      >
        <optgroup label="Comparison">
          {OPERATORS.filter((o) => o.group === "compare").map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </optgroup>
        <optgroup label="Text">
          {OPERATORS.filter((o) => o.group === "text").map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </optgroup>
        <optgroup label="Set">
          {OPERATORS.filter((o) => o.group === "set").map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </optgroup>
        <optgroup label="Date &amp; range">
          {OPERATORS.filter((o) => o.group === "datetime").map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </optgroup>
        <optgroup label="Presence">
          {OPERATORS.filter((o) => o.group === "presence").map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </optgroup>
      </select>
      </div>

      <span className="cond-connector" aria-hidden="true">→</span>

      <div className="cond-field cond-field-value">
        <span className="cond-field-label">Compare to</span>
        {arity !== "none" ? (
        <div className="cond-value">
          <select
            className="cond-value-mode"
            value={mode}
            aria-label="Value source"
            title="Where the comparison value comes from"
            onChange={(e) => setValueMode(e.target.value as ValueMode)}
          >
            <option value="literal">Literal value</option>
            <option value="context">Request context</option>
            <option value="assignment">Assignment attribute</option>
            <option value="system">System value</option>
            <option value="reference">Reference data</option>
          </select>
          {mode === "literal" && arity === "range" ? (
            <div className="cond-range">
              <input
                className="cond-value-input"
                value={key.split(",")[0]?.trim() ?? ""}
                placeholder="min"
                aria-label="Range minimum"
                onChange={(e) =>
                  setValueKey(
                    `${e.target.value}, ${key.split(",")[1]?.trim() ?? ""}`,
                  )
                }
              />
              <span className="cond-range-sep">→</span>
              <input
                className="cond-value-input"
                value={key.split(",")[1]?.trim() ?? ""}
                placeholder="max"
                aria-label="Range maximum"
                onChange={(e) =>
                  setValueKey(
                    `${key.split(",")[0]?.trim() ?? ""}, ${e.target.value}`,
                  )
                }
              />
            </div>
          ) : mode === "system" ? (
            <select
              className="cond-value-input"
              value={key}
              aria-label="System value"
              onChange={(e) => setValueKey(e.target.value)}
            >
              <option value="">Choose…</option>
              {SYSTEM_KEYS.map((k) => (
                <option key={k} value={k}>
                  {k}
                </option>
              ))}
            </select>
          ) : (
            <SuggestInput
              className="cond-value-input"
              value={key}
              suggestions={
                mode === "reference"
                  ? refDataList
                  : mode === "literal"
                    ? []
                    : refList
              }
              type={mode === "literal" && arity === "number" ? "number" : "text"}
              inputMode={
                mode === "literal" && arity === "number" ? "decimal" : undefined
              }
              placeholder={
                mode === "reference"
                  ? "key or key.path"
                  : mode !== "literal"
                    ? "key"
                    : arity === "list"
                      ? "value1, value2"
                      : arity === "number"
                        ? "0"
                        : opMeta?.group === "datetime"
                          ? "2026-01-31"
                          : opMeta?.group === "text" &&
                              (node.operator === "matches" ||
                                node.operator === "notMatches")
                            ? "^[A-Z]{2}$"
                            : "value"
              }
              ariaLabel="Value"
              onChange={setValueKey}
            />
          )}
          {mode !== "literal" && (
            <span
              className="cond-ref-badge"
              title="References another attribute at evaluation time"
            >
              ref
            </span>
          )}
        </div>
      ) : (
        <span className="cond-value cond-value-none">no value needed</span>
      )}
      </div>
      </div>

      <div className="cond-rule-tools">
        <button
          type="button"
          className="cond-tool-btn"
          title="Duplicate condition"
          onClick={onDuplicate}
        >
          <AppIcon name="copy" size={15} />
          <span>Duplicate</span>
        </button>
        <button
          type="button"
          className="cond-tool-btn danger"
          title="Remove condition"
          onClick={onRemove}
        >
          <span aria-hidden="true">✕</span>
          <span>Remove</span>
        </button>
      </div>

      {hasError && (
        <p className="cond-row-error" role="alert">
          <AppIcon name="denied" size={14} />
          <span>{issues.join(" ")}</span>
        </p>
      )}
    </div>
  );
}
