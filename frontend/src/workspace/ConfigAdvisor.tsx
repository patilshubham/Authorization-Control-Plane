import { useMemo, useState, type ReactNode } from "react";
import { useConfigFindings, useSummarizeConfigFindings } from "../api/hooks";
import { useAiFeature } from "../api/aiConfig";
import { useCapabilities } from "../capabilities";
import { AppIcon } from "../components/icons";
import { RiskDot, Spinner } from "../components/primitives";
import { StatusChip } from "../ui";
import { userFacingError, type ConfigFinding } from "../apiClient";
import { useToast } from "../components/Toast";
import type { Selection } from "./selection";

// F6 — Config Advisor. The deterministic findings list is authoritative and
// renders even when AI is off; the "Summarize with AI" action only ranks and
// explains the same findings (advisory, never blocking).

function findingSelection(finding: ConfigFinding): Selection | null {
  if (!finding.entityKey) return null;
  switch (finding.entityType) {
    case "ROLE":
      return { kind: "role", key: finding.entityKey };
    case "PERMISSION":
      return { kind: "permission", key: finding.entityKey };
    case "POLICY":
      return { kind: "policy", key: finding.entityKey };
    default:
      return null;
  }
}

export function ConfigAdvisor({
  appId,
  onSelect,
}: {
  appId: string;
  onSelect: (s: Selection) => void;
}) {
  const canView = useCapabilities().can("ReadOnlyView", appId);
  const aiEnabled = useAiFeature("configAdvisor");
  const findingsQuery = useConfigFindings(appId, canView);
  const summarize = useSummarizeConfigFindings();
  const toast = useToast();
  const [summary, setSummary] = useState<string | null>(null);
  const [ranked, setRanked] = useState<ConfigFinding[] | null>(null);

  if (!canView) return null;

  const findings = ranked ?? findingsQuery.data?.findings ?? [];
  const highCount = findings.filter((f) => f.severity === "HIGH").length;

  async function summarizeWithAi() {
    // Clear the previous summary/ranking up front so stale output never lingers
    // if the re-run fails (the deterministic findings list still shows).
    setSummary(null);
    setRanked(null);
    try {
      const result = await summarize.mutateAsync(appId);
      setSummary(result.summary);
      setRanked(result.findings);
    } catch (error) {
      toast.error(userFacingError(error));
    }
  }

  return (
    <section
      className="dash-card dash-card-wide advisor-card"
      aria-label="Config advisor"
    >
      <div className="dash-card-head">
        <h2>
          <AppIcon name="sparkles" size={16} /> Config advisor
        </h2>
        <div className="advisor-head-tools">
          {findings.length > 0 && (
            <span className="muted">
              {findings.length} finding{findings.length === 1 ? "" : "s"}
              {highCount > 0 ? ` · ${highCount} high` : ""}
            </span>
          )}
          {aiEnabled && (
            <button
              type="button"
              className="mini-btn is-active"
              disabled={
                summarize.isPending ||
                findingsQuery.isLoading ||
                findings.length === 0
              }
              onClick={summarizeWithAi}
            >
              <AppIcon name="sparkles" size={14} />{" "}
              {summarize.isPending ? "Summarizing…" : "Summarize with AI"}
            </button>
          )}
        </div>
      </div>

      {findingsQuery.isLoading ? (
        <Spinner label="Scanning configuration…" />
      ) : findingsQuery.isError ? (
        <p className="muted">Could not load advisor findings.</p>
      ) : findings.length === 0 ? (
        <p className="advisor-empty">
          <AppIcon name="active" size={16} /> No governance smells detected. This
          configuration looks healthy.
        </p>
      ) : (
        <>
          {summary && <AdvisorSummary text={summary} />}
          <ul className="advisor-list">
            {findings.map((finding) => {
              const sel = findingSelection(finding);
              return (
                <li key={finding.id} className="advisor-item">
                  <span className="advisor-sev" title={`${finding.severity} severity`}>
                    <RiskDot level={finding.severity} />
                    <StatusChip value={finding.severity} />
                  </span>
                  <span className="advisor-body">
                    <span className="advisor-title">{finding.title}</span>
                    <span className="advisor-detail muted">{finding.detail}</span>
                    {finding.suggestedFix && (
                      <span className="advisor-fix">
                        <strong>Suggested fix:</strong> {finding.suggestedFix}
                      </span>
                    )}
                  </span>
                  {sel && (
                    <button
                      type="button"
                      className="mini-btn ghost advisor-open"
                      onClick={() => onSelect(sel)}
                    >
                      Open
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
          {summary && (
            <p className="cond-ai-hint">
              AI-ranked and explained — advisory only. Findings are detected
              without AI.
            </p>
          )}
        </>
      )}
    </section>
  );
}

type SummaryBlock =
  | { type: "heading"; text: string }
  | { type: "list"; items: string[] }
  | { type: "para"; text: string };

// Parses the advisor summary (lightweight Markdown: "## " section headings, "- "/"* " bullets, and
// inline **bold**/`code`) into blocks. Plain text with no markers renders as paragraphs, so a
// non-structured model reply still displays cleanly.
function parseSummaryBlocks(markdown: string): SummaryBlock[] {
  const blocks: SummaryBlock[] = [];
  let para: string[] = [];
  let list: string[] = [];
  const flushPara = () => {
    if (para.length) {
      blocks.push({ type: "para", text: para.join(" ") });
      para = [];
    }
  };
  const flushList = () => {
    if (list.length) {
      blocks.push({ type: "list", items: list });
      list = [];
    }
  };

  for (const raw of markdown.replace(/\r\n/g, "\n").split("\n")) {
    const line = raw.trim();
    if (line === "") {
      flushPara();
      flushList();
    } else if (line.startsWith("## ") || line.startsWith("### ")) {
      flushPara();
      flushList();
      blocks.push({ type: "heading", text: line.replace(/^#{2,3}\s+/, "") });
    } else if (line.startsWith("- ") || line.startsWith("* ")) {
      flushPara();
      list.push(line.slice(2).trim());
    } else {
      flushList();
      para.push(line);
    }
  }
  flushPara();
  flushList();
  return blocks;
}

// Renders inline **bold** and `code` spans as React nodes. Text-only (never dangerouslySetInnerHTML),
// so model output can never inject markup.
function renderInline(text: string): ReactNode[] {
  const nodes: ReactNode[] = [];
  const pattern = /\*\*(.+?)\*\*|`([^`]+?)`/g;
  let lastIndex = 0;
  let key = 0;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(text)) !== null) {
    if (match.index > lastIndex) {
      nodes.push(text.slice(lastIndex, match.index));
    }
    if (match[1] !== undefined) {
      nodes.push(<strong key={key++}>{match[1]}</strong>);
    } else if (match[2] !== undefined) {
      nodes.push(<code key={key++}>{match[2]}</code>);
    }
    lastIndex = match.index + match[0].length;
  }
  if (lastIndex < text.length) {
    nodes.push(text.slice(lastIndex));
  }
  return nodes;
}

// Renders the AI insights summary as a structured, scannable review (Executive Summary, Key
// Observations, Potential Impact, Recommended Actions, Overall Assessment) instead of one flat blob.
export function AdvisorSummary({ text }: { text: string }) {
  const blocks = useMemo(() => parseSummaryBlocks(text), [text]);
  return (
    <div className="advisor-summary advisor-summary-rich" aria-live="polite">
      {blocks.map((block, index) => {
        if (block.type === "heading") {
          return (
            <h4 key={index} className="advisor-sum-h">
              {renderInline(block.text)}
            </h4>
          );
        }
        if (block.type === "list") {
          return (
            <ul key={index} className="advisor-sum-list">
              {block.items.map((item, itemIndex) => (
                <li key={itemIndex}>{renderInline(item)}</li>
              ))}
            </ul>
          );
        }
        return (
          <p key={index} className="advisor-sum-p">
            {renderInline(block.text)}
          </p>
        );
      })}
    </div>
  );
}
