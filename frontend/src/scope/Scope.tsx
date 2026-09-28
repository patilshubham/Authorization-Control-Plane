import { Link } from "react-router-dom";

// A scope badge makes the *kind* of area an admin is in unmistakable:
// PLATFORM (global) vs APPLICATION (scoped to one app). This is the core of the
// information-architecture goal — scope is always visible.
export function ScopeBadge({
  scope,
  detail,
}: {
  scope: "platform" | "application";
  detail?: string;
}) {
  const label = scope === "platform" ? "Platform" : "Application";
  return (
    <span className={`scope-badge scope-${scope}`} title={`${label} scope`}>
      <span className="scope-badge-dot" aria-hidden="true" />
      <span className="scope-badge-label">{label}</span>
      {detail && <span className="scope-badge-detail">{detail}</span>}
    </span>
  );
}

export type Crumb = { label: string; to?: string };

export function Breadcrumbs({ items }: { items: Crumb[] }) {
  return (
    <nav className="breadcrumbs" aria-label="Breadcrumb">
      <ol>
        {items.map((item, i) => {
          const last = i === items.length - 1;
          return (
            <li
              key={`${item.label}-${i}`}
              aria-current={last ? "page" : undefined}
            >
              {item.to && !last ? (
                <Link to={item.to}>{item.label}</Link>
              ) : (
                <span>{item.label}</span>
              )}
              {!last && (
                <span className="breadcrumbs-sep" aria-hidden="true">
                   &nbsp;&gt;
                </span>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
