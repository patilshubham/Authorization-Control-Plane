import type { ReactElement } from "react";

/**
 * Shared line-icon set for the whole console — sidebar navigation, KPI widgets
 * and stat cards all draw from the same vocabulary so the iconography reads as
 * one cohesive system. Icons are stroke-based and inherit `currentColor`, so
 * they adapt to theme, hover and active states automatically.
 */
const PATHS: Record<string, ReactElement> = {
  overview: (
    <>
      <rect x="3" y="3" width="7" height="8" rx="1.5" />
      <rect x="3" y="14" width="7" height="7" rx="1.5" />
      <rect x="14" y="3" width="7" height="7" rx="1.5" />
      <rect x="14" y="13" width="7" height="8" rx="1.5" />
    </>
  ),
  applications: (
    <>
      <rect x="3" y="3" width="7" height="7" rx="1.5" />
      <rect x="14" y="3" width="7" height="7" rx="1.5" />
      <rect x="3" y="14" width="7" height="7" rx="1.5" />
      <rect x="14" y="14" width="7" height="7" rx="1.5" />
    </>
  ),
  tenants: (
    <>
      <path d="M3 21h18" />
      <path d="M6 21V6a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v15" />
      <path d="M14 21V10h3a1 1 0 0 1 1 1v10" />
      <path d="M9 9h2M9 13h2" />
    </>
  ),
  users: (
    <>
      <circle cx="9" cy="8" r="3" />
      <path d="M3.5 20a5.5 5.5 0 0 1 11 0" />
      <path d="M16 5.2a3 3 0 0 1 0 5.6" />
      <path d="M18 14.5a5.5 5.5 0 0 1 2.5 4.5" />
    </>
  ),
  lens: (
    <>
      <circle cx="11" cy="11" r="6" />
      <path d="M20 20l-3.8-3.8" />
    </>
  ),
  audit: (
    <>
      <circle cx="4.5" cy="6" r="1" />
      <circle cx="4.5" cy="12" r="1" />
      <circle cx="4.5" cy="18" r="1" />
      <path d="M9 6h11M9 12h11M9 18h11" />
    </>
  ),
  settings: (
    <>
      <circle cx="12" cy="12" r="3.2" />
      <path d="M12 2.5v3M12 18.5v3M2.5 12h3M18.5 12h3M5 5l2.1 2.1M16.9 16.9L19 19M19 5l-2.1 2.1M7.1 16.9L5 19" />
    </>
  ),
  roles: <path d="M12 3l7 3v5c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V6z" />,
  permissions: (
    <>
      <circle cx="8" cy="15" r="4" />
      <path d="M11 12l8-8" />
      <path d="M17 6l2 2" />
      <path d="M14 9l2 2" />
    </>
  ),
  policies: (
    <>
      <path d="M7 3h7l4 4v14H7z" />
      <path d="M14 3v4h4" />
      <path d="M10 12h5M10 16h5" />
    </>
  ),
  matrix: (
    <>
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <path d="M3 9h18M3 15h18M9 3v18M15 3v18" />
    </>
  ),
  assignments: (
    <>
      <path d="M10 14a3.5 3.5 0 0 0 5 0l3-3a3.5 3.5 0 0 0-5-5l-1 1" />
      <path d="M14 10a3.5 3.5 0 0 0-5 0l-3 3a3.5 3.5 0 0 0 5 5l1-1" />
    </>
  ),
  identity: (
    <>
      <rect x="3" y="5" width="18" height="14" rx="2" />
      <circle cx="8.5" cy="11" r="2.2" />
      <path d="M5.2 16a3.3 3.3 0 0 1 6.6 0" />
      <path d="M14.5 10h4M14.5 13.5h4" />
    </>
  ),
  simulator: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M10.5 8.5l5 3.5-5 3.5z" />
    </>
  ),
  activity: <path d="M3 12h4l2.5-7 4 14 2.5-7H21" />,
  active: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M8.5 12.5l2.5 2.5 4.5-5" />
    </>
  ),
  search: (
    <>
      <circle cx="11" cy="11" r="6" />
      <path d="M20 20l-3.8-3.8" />
    </>
  ),
  brand: (
    <>
      <path d="M12 3l7 3v5c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V6z" />
      <path d="M9 12l2 2 4-4" />
    </>
  ),
  chevron: <path d="M9 6l6 6-6 6" />,
  copy: (
    <>
      <rect x="9" y="9" width="11" height="11" rx="2" />
      <path d="M5 15V5a2 2 0 0 1 2-2h8" />
    </>
  ),
  empty: (
    <>
      <path d="M4 14l2-9h12l2 9" />
      <path d="M4 14v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4" />
      <path d="M4 14h4l1.5 2.5h5L16 14h4" />
    </>
  ),
  sparkles: (
    <>
      <path d="M12 4l1.6 4.4L18 10l-4.4 1.6L12 16l-1.6-4.4L6 10l4.4-1.6z" />
      <path d="M18 15l.7 1.8L20.5 17.5l-1.8.7L18 20l-.7-1.8L15.5 17.5l1.8-.7z" />
    </>
  ),
  plus: <path d="M12 5v14M5 12h14" />,
  reference: (
    <>
      <path d="M4 5.5A1.5 1.5 0 0 1 5.5 4H12v16H5.5A1.5 1.5 0 0 1 4 18.5z" />
      <path d="M20 5.5A1.5 1.5 0 0 0 18.5 4H12v16h6.5a1.5 1.5 0 0 0 1.5-1.5z" />
      <path d="M7 8h2.5M7 11h2.5M14.5 8H17M14.5 11H17" />
    </>
  ),
  denied: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M9 9l6 6M15 9l-6 6" />
    </>
  ),
  info: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v5" />
      <path d="M12 7.6h.01" />
    </>
  ),
  bulb: (
    <>
      <path d="M9 18h6" />
      <path d="M10 21h4" />
      <path d="M12 3a6 6 0 0 0-3.6 10.8c.5.4.85 1 .95 1.65l.15.55h5l.15-.55c.1-.65.45-1.25.95-1.65A6 6 0 0 0 12 3z" />
    </>
  ),
  code: (
    <>
      <path d="M9 8l-4 4 4 4" />
      <path d="M15 8l4 4-4 4" />
    </>
  ),
  upload: (
    <>
      <path d="M12 15V4" />
      <path d="M8 8l4-4 4 4" />
      <path d="M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3" />
    </>
  ),
  lock: (
    <>
      <rect x="5" y="11" width="14" height="9" rx="2" />
      <path d="M8 11V7a4 4 0 0 1 8 0v4" />
    </>
  ),
};

// Aliases so nav labels and metric labels can share the same glyphs.
PATHS.dashboard = PATHS.overview;

export type IconName = keyof typeof PATHS | string;

export function AppIcon({ name, size = 20 }: { name: string; size?: number }) {
  const inner = PATHS[name];
  if (!inner) return null;
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.7}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {inner}
    </svg>
  );
}
