// Shared building blocks for the D3 node-link visualizations.
//
// These keep the graph views visually consistent with the Access Lens columns
// (same iconography, same risk semantics) while staying self-contained so any
// page can render a hierarchy without depending on the Lens internals.

export type GraphNodeKind =
  "root" | "tenant" | "application" | "role" | "permission" | "policy";

export type GraphNodeDatum = {
  id: string;
  kind: GraphNodeKind;
  label: string;
  sublabel?: string;
  riskLevel?: string;
  effect?: string;
  /** Extra count shown when a branch is collapsed / not expanded. */
  badge?: number;
  children?: GraphNodeDatum[];
};

/** Compact inline glyph per node kind — inherits `currentColor`. */
export function NodeGlyph({ kind }: { kind: GraphNodeKind }) {
  const common = {
    width: 15,
    height: 15,
    viewBox: "0 0 16 16",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 1.4,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    "aria-hidden": true,
  };
  switch (kind) {
    case "tenant":
      return (
        <svg {...common}>
          <path d="M2.5 13.5h11" />
          <path d="M3.5 13.5V4l4.5-2 4.5 2v9.5" />
          <path d="M6 6.5h1.5M9 6.5h1.5M6 9h1.5M9 9h1.5" />
        </svg>
      );
    case "application":
      return (
        <svg {...common}>
          <path d="M8 1.8 14 5 8 8.2 2 5z" />
          <path d="M2 8l6 3.2L14 8" />
          <path d="M2 11l6 3.2L14 11" />
        </svg>
      );
    case "role":
      return (
        <svg {...common}>
          <path d="M8 1.6 13 3.4v4.2c0 3.2-2.1 5.4-5 6.8-2.9-1.4-5-3.6-5-6.8V3.4z" />
          <path d="M5.8 7.9 7.4 9.5 10.4 6" />
        </svg>
      );
    case "permission":
      return (
        <svg {...common}>
          <circle cx="5.4" cy="5.4" r="2.6" />
          <path d="M7.3 7.3 13 13M11 11l1.6-1.6M9.4 9.4 11 7.8" />
        </svg>
      );
    case "policy":
      return (
        <svg {...common}>
          <path d="M4 2h6l2.5 2.5V14H4z" />
          <path d="M9.6 2v3H12.5" />
          <path d="M6 8h4M6 10.5h4" />
        </svg>
      );
    case "root":
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="5.5" />
          <path d="M8 2.5v11M2.5 8h11" />
        </svg>
      );
  }
}

/** Map a node kind to a design-token color drawn from the chart theme. */
export function nodeColor(
  kind: GraphNodeKind,
  t: {
    accent: string;
    info: string;
    warning: string;
    success: string;
    textH: string;
    muted: string;
  },
): string {
  switch (kind) {
    case "tenant":
      return t.textH;
    case "application":
      return t.accent;
    case "role":
      return t.info;
    case "permission":
      return t.warning;
    case "policy":
      return t.success;
    case "root":
      return t.muted;
  }
}
