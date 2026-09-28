import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { getDisplayName } from "../auth";
import { AppIcon } from "../components/icons";
import { platformPaths } from "../workspace/nav";
import { usePortal } from "./PortalContext";

function initialsOf(name: string): string {
  const cleaned = name
    .split("@")[0]
    .replace(/[._-]+/g, " ")
    .trim();
  const parts = cleaned.split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

/**
 * Account control in the top bar. Replaces the old "click-to-logout" chip with a
 * proper menu: it surfaces the signed-in identity, a link to the profile page,
 * and an explicit sign-out action so logout is deliberate rather than accidental.
 */
export function UserMenu() {
  const { user, onLogout } = usePortal();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  const displayName = getDisplayName(user);
  const email = (user?.profile?.email as string | undefined) ?? displayName;

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node))
        setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div className="user-menu" ref={rootRef}>
      <button
        type="button"
        className="user-chip"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
        title="Account"
      >
        <span className="user-chip-avatar" aria-hidden="true">
          {initialsOf(displayName)}
        </span>
        <span className="user-chip-name">{displayName}</span>
        <span className="user-chip-caret" aria-hidden="true">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none">
            <path
              d="M6 9l6 6 6-6"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        </span>
      </button>

      {open && (
        <div className="user-menu-pop" role="menu">
          <div className="user-menu-head">
            <span className="user-menu-avatar" aria-hidden="true">
              {initialsOf(displayName)}
            </span>
            <div className="user-menu-id">
              <span className="user-menu-name">{displayName}</span>
              <span className="user-menu-email">{email}</span>
            </div>
          </div>

          <div className="user-menu-sep" />

          <button
            type="button"
            role="menuitem"
            className="user-menu-item"
            onClick={() => {
              setOpen(false);
              navigate(platformPaths.profile);
            }}
          >
            <span className="user-menu-item-icon" aria-hidden="true">
              <AppIcon name="identity" size={17} />
            </span>
            <span>
              <span className="user-menu-item-label">Your profile</span>
              <span className="user-menu-item-hint">
                Roles, access & what you can do
              </span>
            </span>
          </button>

          <div className="user-menu-sep" />

          <button
            type="button"
            role="menuitem"
            className="user-menu-item danger"
            onClick={() => {
              setOpen(false);
              onLogout();
            }}
          >
            <span className="user-menu-item-icon" aria-hidden="true">
              <svg width="17" height="17" viewBox="0 0 24 24" fill="none">
                <path
                  d="M15 12H4m0 0l3.5-3.5M4 12l3.5 3.5"
                  stroke="currentColor"
                  strokeWidth="1.7"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
                <path
                  d="M11 4h6a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6"
                  stroke="currentColor"
                  strokeWidth="1.7"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
              </svg>
            </span>
            <span className="user-menu-item-label">Sign out</span>
          </button>
        </div>
      )}
    </div>
  );
}
