// Domain constant option lists (fixed enums defined by the functional specification).
// These are not backend-fetched reference data; they are stable parts of the domain model.

/** Risk levels — FR-001 / FR-007 / FR-009. */
export const RISK_LEVELS = ["LOW", "MEDIUM", "HIGH", "CRITICAL"] as const;
export type RiskLevel = (typeof RISK_LEVELS)[number];
export const DEFAULT_RISK_LEVEL: RiskLevel = "MEDIUM";

/** Policy effects — FR-016. */
export const POLICY_EFFECTS = ["ALLOW", "DENY"] as const;
export type PolicyEffect = (typeof POLICY_EFFECTS)[number];

/** Policy combining algorithms — how overlapping policy decisions are reconciled. */
export const POLICY_COMBINING_ALGORITHMS = [
  { value: "deny-overrides", label: "Deny overrides" },
  { value: "allow-overrides", label: "Allow overrides" },
  { value: "first-applicable", label: "First applicable" },
] as const;
export const DEFAULT_POLICY_COMBINING_ALGORITHM = "deny-overrides";

/** Governance lifecycle statuses for roles and permissions (authz check constraints). */
export const GOVERNANCE_STATUSES = [
  "ACTIVE",
  "DISABLED",
  "ARCHIVED",
  "DEPRECATED",
] as const;

/** Policy draft/publish workflow states. */
export const POLICY_STATES = ["DRAFT", "PUBLISHED"] as const;

/** Derived display states for an assignment (see AssignmentDisplayStatus). */
export const ASSIGNMENT_STATES = ["ACTIVE", "EXPIRED", "REVOKED"] as const;

/** Assignment expiry buckets used by the Assignments filter. */
export const ASSIGNMENT_EXPIRY_BUCKETS = [
  { value: "none", label: "No expiry" },
  { value: "expired", label: "Expired" },
  { value: "soon", label: "Expiring soon" },
  { value: "later", label: "Expires later" },
] as const;

/**
 * Audit/activity event categories. Derived from the event-type prefix taxonomy
 * (see categoryOf() in workspace/activity.ts and ApplyCategoryFilter server-side).
 */
export const AUDIT_CATEGORIES = [
  "Application",
  "Assignment",
  "Identity",
  "Mapping",
  "Permission",
  "Policy",
  "Role",
  "Other",
] as const;

/**
 * OIDC token signing algorithms — FR-004 (allowed algorithms) / AR-15.
 * Federated providers publish public keys via JWKS, so only asymmetric JOSE
 * signature algorithms are offered. Symmetric HMAC (HS*) is excluded because it
 * relies on a shared secret, and `none` is never permitted (rejected by the API).
 * Grouped by cryptographic family for the picker.
 */
export const OIDC_ALGORITHM_GROUPS = [
  {
    family: "RSASSA-PKCS1-v1_5",
    description: "Widely supported RSA signatures.",
    algorithms: ["RS256", "RS384", "RS512"],
  },
  {
    family: "ECDSA",
    description: "Elliptic-curve signatures — compact keys.",
    algorithms: ["ES256", "ES384", "ES512", "ES256K"],
  },
  {
    family: "RSASSA-PSS",
    description: "Modern RSA signatures with salting.",
    algorithms: ["PS256", "PS384", "PS512"],
  },
  {
    family: "EdDSA",
    description: "Edwards-curve signatures (Ed25519 / Ed448).",
    algorithms: ["EdDSA"],
  },
] as const;

/** Flat list of every selectable OIDC signing algorithm. */
export const OIDC_ALGORITHMS = OIDC_ALGORITHM_GROUPS.flatMap(
  (group) => group.algorithms,
);

/** Default algorithm applied when none is chosen (matches API default). */
export const DEFAULT_OIDC_ALGORITHM = "RS256";
