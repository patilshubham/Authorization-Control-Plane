// Workspace location = the single "where am I" tuple for the console: which
// application is selected and which module/entity is in view. It is serialised
// to the URL query string so that refresh, deep links, and browser Back/Forward
// all preserve context. Kept deliberately router-free — the console is a single
// screen, so a full routing library would be more machinery than the model needs.
import type { Selection } from "./selection";

export type WorkspaceLocation = { appId: string; selection: Selection };

// Views that carry an app-scoped entity key.
const KEYED_KINDS = new Set<Selection["kind"]>([
  "role",
  "permission",
  "policy",
]);

// Every module/entity view the console can address. Anything unknown falls back
// to the dashboard so a hand-edited or stale URL can never wedge the app.
const KNOWN_KINDS = new Set<Selection["kind"]>([
  "dashboard",
  "explorer",
  "tenants",
  "users",
  "user",
  "matrix",
  "access",
  "identity",
  "simulator",
  "activity",
  "application",
  "role",
  "permission",
  "policy",
]);

/** Build a Selection from raw URL params, tolerating anything malformed. */
export function selectionFromParams(
  view: string,
  key: string,
  email: string,
): Selection {
  const kind = view as Selection["kind"];
  if (!KNOWN_KINDS.has(kind)) return { kind: "dashboard" };
  if (KEYED_KINDS.has(kind))
    return key ? ({ kind, key } as Selection) : { kind: "dashboard" };
  if (kind === "user")
    return email ? { kind: "user", email } : { kind: "users" };
  return { kind } as Selection;
}

/** Read the current window location into a WorkspaceLocation. */
export function readLocation(): WorkspaceLocation {
  const p = new URLSearchParams(window.location.search);
  return {
    appId: p.get("app") ?? "",
    selection: selectionFromParams(
      p.get("view") ?? "dashboard",
      p.get("key") ?? "",
      p.get("email") ?? "",
    ),
  };
}

/** Serialise a WorkspaceLocation to a query string (no leading "?"). */
export function locationToQuery(appId: string, selection: Selection): string {
  const p = new URLSearchParams();
  if (appId) p.set("app", appId);
  p.set("view", selection.kind);
  if ("key" in selection && selection.key) p.set("key", selection.key);
  if (selection.kind === "user" && selection.email)
    p.set("email", selection.email);
  return p.toString();
}

/** True when a selection is scoped to a specific application-owned entity. */
export function isAppScopedEntity(selection: Selection): boolean {
  return KEYED_KINDS.has(selection.kind);
}
