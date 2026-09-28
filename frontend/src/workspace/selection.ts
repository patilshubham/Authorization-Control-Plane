// The single unit of "what am I looking at" for the whole workspace.
export type Selection =
  | { kind: "role"; key: string }
  | { kind: "permission"; key: string }
  | { kind: "policy"; key: string }
  | { kind: "application" }
  | { kind: "dashboard" }
  | { kind: "access" }
  | { kind: "identity" }
  | { kind: "activity" }
  | { kind: "matrix" }
  | { kind: "simulator" }
  | { kind: "tenants" }
  | { kind: "explorer" }
  | { kind: "users" }
  | { kind: "user"; email: string };

export type SectionKind = Selection["kind"];
