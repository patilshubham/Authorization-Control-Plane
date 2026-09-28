// Shared presentation helpers for audit/activity events. Used by both the
// Dashboard recent-activity feed and the Activity timeline so event styling
// stays consistent across the console.

import { getRuntimeConfig } from "../api/runtimeConfig";

export type ActivityTone =
  "success" | "accent" | "warning" | "danger" | "muted";

export type ActivityMeta = {
  icon: string;
  tone: ActivityTone;
  label: string;
  category: string;
};

/** Classify an audit event type into an icon, colour tone and readable label. */
export function describeEvent(eventType: string): ActivityMeta {
  const type = eventType.toUpperCase();
  const label = humanizeEventType(eventType);
  const category = categoryOf(type);

  if (/(REVOKED|DELETED|REMOVED|UNMAPPED|ARCHIVED)/.test(type)) {
    return { icon: "✕", tone: "danger", label, category };
  }
  if (/(DISABLED|SUSPENDED|EXPIRED)/.test(type)) {
    return { icon: "⏸", tone: "warning", label, category };
  }
  if (/(PUBLISHED|ENABLED|APPROVED|ACTIVATED)/.test(type)) {
    return { icon: "▲", tone: "accent", label, category };
  }
  if (/(CREATED|GRANTED|REGISTERED|ADDED)/.test(type)) {
    return { icon: "+", tone: "success", label, category };
  }
  if (/(UPDATED|EXTENDED|CHANGED|MODIFIED)/.test(type)) {
    return { icon: "✎", tone: "warning", label, category };
  }
  return { icon: "•", tone: "muted", label, category };
}

// Known role identifiers (platform + delegated app roles) mapped to friendly
// labels. The server owns these via `GET /v1/config`; we read the runtime
// singleton so operators can curate labels without a frontend rebuild.

/** Map a raw actor-role identifier to a friendly label, humanising unknown values. */
export function roleLabel(role: string | null | undefined): string | null {
  if (!role) return null;
  const key = role.replace(/[^a-z0-9]/gi, "").toLowerCase();
  const labels = getRuntimeConfig().roleLabels;
  if (labels[key]) return labels[key];
  return role
    .replace(/[_\-.]+/g, " ")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .trim();
}

/** Format an actor email with its role in parentheses, e.g. "admin@local.test (Platform Super Admin)". */
export function actorLabel(
  email: string | null | undefined,
  role?: string | null,
): string {
  const who = email && email.trim() ? email : "system";
  const label = roleLabel(role);
  return label ? `${who} (${label})` : who;
}

function humanizeEventType(eventType: string): string {
  const spaced = eventType
    .replace(/[_\-.]+/g, " ")
    .trim()
    .toLowerCase();
  if (!spaced) return eventType;
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function categoryOf(type: string): string {
  if (type.startsWith("ROLE_PERMISSION")) return "Mapping";
  if (type.startsWith("ROLE")) return "Role";
  if (type.startsWith("PERMISSION")) return "Permission";
  if (type.startsWith("POLICY")) return "Policy";
  if (type.startsWith("ASSIGNMENT")) return "Assignment";
  if (type.startsWith("APPLICATION")) return "Application";
  if (type.startsWith("OIDC") || type.includes("PROVIDER")) return "Identity";
  return "Other";
}

/** Compact relative timestamp, e.g. "just now", "5m ago", "3h ago", "2d ago". */
export function relativeTime(iso: string, now: number = Date.now()): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "";
  const diff = Math.max(0, now - then);
  const s = Math.floor(diff / 1000);
  if (s < 45) return "just now";
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ago`;
  const d = Math.floor(h / 24);
  if (d < 7) return `${d}d ago`;
  const w = Math.floor(d / 7);
  if (w < 5) return `${w}w ago`;
  const mo = Math.floor(d / 30);
  if (mo < 12) return `${mo}mo ago`;
  return `${Math.floor(d / 365)}y ago`;
}

/** Human day bucket label for grouping ("Today", "Yesterday", or a date). */
export function dayBucket(iso: string, now: number = Date.now()): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "Unknown";
  const startOfDay = (t: number) => {
    const d = new Date(t);
    d.setHours(0, 0, 0, 0);
    return d.getTime();
  };
  const today = startOfDay(now);
  const target = startOfDay(date.getTime());
  const dayMs = 86_400_000;
  if (target === today) return "Today";
  if (target === today - dayMs) return "Yesterday";
  return date.toLocaleDateString(undefined, {
    weekday: "short",
    month: "short",
    day: "numeric",
  });
}
