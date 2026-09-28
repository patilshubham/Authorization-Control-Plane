// D3BipartiteGraph — a two-column node-link view for many-to-many relationships
// (Role ↔ Permission coverage, Subject ↔ Role assignments).
//
// Follows the "D3 computes, React renders" pattern: d3-scale places the two
// columns of nodes and d3-shape draws the connecting links, but every element is
// plain React SVG so it stays themeable and testable in jsdom. Hovering or
// focusing a node highlights only its links and fades the rest.

import { useMemo, useState } from "react";
import { scalePoint } from "d3-scale";
import { linkHorizontal } from "d3-shape";
import { useChartTheme } from "../charts";
import { nodeColor, type GraphNodeKind } from "./nodes";

export type BipartiteNode = {
  id: string;
  label: string;
  sublabel?: string;
  kind: GraphNodeKind;
  riskLevel?: string;
};

export type BipartiteLinkState =
  "published" | "draft" | "active" | "expired" | "revoked";

export type BipartiteLink = {
  source: string; // left node id
  target: string; // right node id
  state?: BipartiteLinkState;
};

const NODE_W = 190;
const NODE_H = 34;
const ROW_GAP = 12;
const COL_GAP = 240; // horizontal space between the two columns
const PAD = 20;
const LABEL_MAX = 26;

function truncate(text: string, max = LABEL_MAX): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

export function D3BipartiteGraph({
  left,
  right,
  links,
  leftLabel,
  rightLabel,
  height = 520,
  onSelect,
  ariaLabel = "Relationship graph",
}: {
  left: BipartiteNode[];
  right: BipartiteNode[];
  links: BipartiteLink[];
  leftLabel?: string;
  rightLabel?: string;
  height?: number;
  onSelect?: (node: BipartiteNode) => void;
  ariaLabel?: string;
}) {
  const t = useChartTheme();
  const [active, setActive] = useState<string | null>(null);

  const layout = useMemo(() => {
    const rows = Math.max(left.length, right.length, 1);
    const contentH = rows * (NODE_H + ROW_GAP) + PAD * 2 + 20;
    const yLeft = scalePoint<string>()
      .domain(left.map((n) => n.id))
      .range([PAD + 30, PAD + 30 + (left.length - 1) * (NODE_H + ROW_GAP)])
      .padding(0);
    const yRight = scalePoint<string>()
      .domain(right.map((n) => n.id))
      .range([PAD + 30, PAD + 30 + (right.length - 1) * (NODE_H + ROW_GAP)])
      .padding(0);

    const leftX = PAD;
    const rightX = PAD + NODE_W + COL_GAP;
    const linkGen = linkHorizontal<unknown, { x: number; y: number }>()
      .source((d) => (d as { s: { x: number; y: number } }).s)
      .target((d) => (d as { t: { x: number; y: number } }).t)
      .x((p) => (p as { x: number; y: number }).x)
      .y((p) => (p as { x: number; y: number }).y);

    const drawn = links
      .map((l, i) => {
        const ly = yLeft(l.source);
        const ry = yRight(l.target);
        if (ly == null || ry == null) return null;
        const s = { x: leftX + NODE_W, y: ly + NODE_H / 2 };
        const tp = { x: rightX, y: ry + NODE_H / 2 };
        return {
          key: `${l.source}->${l.target}:${i}`,
          d: linkGen({ s, t: tp } as unknown) ?? "",
          state: l.state,
          source: l.source,
          target: l.target,
        };
      })
      .filter((x): x is NonNullable<typeof x> => x != null);

    return {
      contentW: rightX + NODE_W + PAD,
      contentH,
      leftX,
      rightX,
      yLeft,
      yRight,
      links: drawn,
    };
  }, [left, right, links]);

  const linkStroke = (state?: BipartiteLinkState): string => {
    switch (state) {
      case "published":
      case "active":
        return t.success;
      case "draft":
        return t.warning;
      case "expired":
        return t.muted;
      case "revoked":
        return t.danger;
      default:
        return t.border;
    }
  };

  const isDimmed = (nodeId: string): boolean =>
    active != null &&
    active !== nodeId &&
    !layout.links.some(
      (l) =>
        (l.source === active && l.target === nodeId) ||
        (l.target === active && l.source === nodeId),
    );

  const linkDimmed = (source: string, target: string): boolean =>
    active != null && active !== source && active !== target;

  const renderColumn = (nodes: BipartiteNode[], side: "left" | "right") => {
    const x = side === "left" ? layout.leftX : layout.rightX;
    const scale = side === "left" ? layout.yLeft : layout.yRight;
    return nodes.map((n) => {
      const y = scale(n.id);
      if (y == null) return null;
      const color = nodeColor(n.kind, t);
      const interactive = !!onSelect;
      return (
        <g
          key={n.id}
          className={`d3bip-node${isDimmed(n.id) ? " is-dimmed" : ""}${interactive ? " is-interactive" : ""}`}
          transform={`translate(${x},${y})`}
          role="listitem"
          aria-label={`${n.kind}: ${n.label}`}
          tabIndex={interactive ? 0 : -1}
          onMouseEnter={() => setActive(n.id)}
          onMouseLeave={() => setActive(null)}
          onFocus={() => setActive(n.id)}
          onBlur={() => setActive(null)}
          onClick={interactive ? () => onSelect?.(n) : undefined}
          onKeyDown={
            interactive
              ? (e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    onSelect?.(n);
                  }
                }
              : undefined
          }
        >
          <rect
            className="d3bip-card"
            x={0}
            y={0}
            width={NODE_W}
            height={NODE_H}
            rx={8}
          />
          <rect
            className="d3bip-accent"
            x={side === "left" ? NODE_W - 4 : 0}
            y={0}
            width={4}
            height={NODE_H}
            rx={2}
            fill={color}
          />
          <text
            className="d3bip-label"
            x={12}
            y={n.sublabel ? NODE_H / 2 - 3 : NODE_H / 2 + 4}
          >
            {truncate(n.label)}
          </text>
          {n.sublabel && (
            <text className="d3bip-sub" x={12} y={NODE_H / 2 + 11}>
              {truncate(n.sublabel)}
            </text>
          )}
          {n.riskLevel && (
            <circle
              className="d3bip-risk"
              cx={NODE_W - 12}
              cy={11}
              r={4}
              fill={
                t.risk[n.riskLevel.toUpperCase() as keyof typeof t.risk] ??
                t.muted
              }
            />
          )}
        </g>
      );
    });
  };

  return (
    <div className="d3bipartite" style={{ height }}>
      <svg
        className="d3bip-svg"
        width="100%"
        height={height}
        viewBox={`0 0 ${Math.max(layout.contentW, 320)} ${Math.max(layout.contentH, 200)}`}
        preserveAspectRatio="xMidYMin meet"
        role="group"
        aria-label={ariaLabel}
      >
        {(leftLabel || rightLabel) && (
          <>
            <text className="d3bip-col-head" x={layout.leftX} y={PAD}>
              {leftLabel}
            </text>
            <text className="d3bip-col-head" x={layout.rightX} y={PAD}>
              {rightLabel}
            </text>
          </>
        )}
        <g className="d3bip-links">
          {layout.links.map((l) => (
            <path
              key={l.key}
              className={`d3bip-link${linkDimmed(l.source, l.target) ? " is-dimmed" : ""}`}
              d={l.d}
              fill="none"
              stroke={linkStroke(l.state)}
            />
          ))}
        </g>
        {renderColumn(left, "left")}
        {renderColumn(right, "right")}
      </svg>
    </div>
  );
}
