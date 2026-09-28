import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAssignments, useMappings, usePolicies } from "../api/hooks";
import { appPaths } from "../workspace/nav";
import { AppIcon } from "./icons";

// P10 — Notifications center. A lightweight, app-scoped "needs attention" bell
// derived from data the workspace already caches (no extra backend/store): grants
// expiring soon, and draft policies/grants awaiting publish. Each item deep-links
// to the relevant filtered view. Cross-app/global notifications and delivery
// (email/webhook digests) are intentionally out of scope here.

const DAY_MS = 86_400_000;

type Notice = { id: string; icon: string; label: string; to: string };

export function NotificationsBell({ appId }: { appId: string }) {
  const navigate = useNavigate();
  const assignments = useAssignments(appId);
  const policies = usePolicies(appId);
  const mappings = useMappings(appId);
  const [open, setOpen] = useState(false);

  const notices = useMemo<Notice[]>(() => {
    const list: Notice[] = [];

    const expiring = (assignments.data ?? []).filter((a) => {
      if (!a.validUntil || (a.state ?? "").toUpperCase() !== "ACTIVE") {
        return false;
      }
      const days = Math.ceil(
        (new Date(a.validUntil).getTime() - Date.now()) / DAY_MS,
      );
      return days >= 0 && days <= 7;
    }).length;
    if (expiring > 0) {
      list.push({
        id: "expiring",
        icon: "active",
        label: `${expiring} grant${expiring === 1 ? "" : "s"} expiring within 7 days`,
        to: `${appPaths.assignments(appId)}?expiry=soon`,
      });
    }

    const draftPolicies = (policies.data ?? []).filter(
      (p) => p.state !== "PUBLISHED",
    ).length;
    if (draftPolicies > 0) {
      list.push({
        id: "draft-policies",
        icon: "policies",
        label: `${draftPolicies} draft polic${draftPolicies === 1 ? "y" : "ies"} awaiting publish`,
        to: `${appPaths.policies(appId)}?state=DRAFT`,
      });
    }

    const draftGrants = (mappings.data ?? []).filter(
      (m) => m.state !== "PUBLISHED",
    ).length;
    if (draftGrants > 0) {
      list.push({
        id: "draft-grants",
        icon: "matrix",
        label: `${draftGrants} draft grant${draftGrants === 1 ? "" : "s"} awaiting publish`,
        to: appPaths.matrix(appId),
      });
    }

    return list;
  }, [assignments.data, policies.data, mappings.data, appId]);

  const go = (to: string) => {
    setOpen(false);
    navigate(to);
  };

  return (
    <div className="notif">
      <button
        type="button"
        className="notif-btn"
        aria-label={`Notifications${notices.length ? ` (${notices.length})` : ""}`}
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true">
          <path
            fill="none"
            stroke="currentColor"
            strokeWidth="1.8"
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M18 8a6 6 0 1 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"
          />
        </svg>
        {notices.length > 0 && (
          <span className="notif-badge" aria-hidden="true">
            {notices.length}
          </span>
        )}
      </button>

      {open && (
        <>
          <button
            type="button"
            className="notif-backdrop"
            aria-label="Close notifications"
            onClick={() => setOpen(false)}
          />
          <div className="notif-panel" role="menu">
            <div className="notif-panel-head">Needs attention</div>
            {notices.length === 0 ? (
              <p className="notif-empty muted">You’re all caught up.</p>
            ) : (
              <ul className="notif-list">
                {notices.map((n) => (
                  <li key={n.id}>
                    <button
                      type="button"
                      className="notif-item"
                      role="menuitem"
                      onClick={() => go(n.to)}
                    >
                      <AppIcon name={n.icon} size={15} />
                      <span>{n.label}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </>
      )}
    </div>
  );
}
