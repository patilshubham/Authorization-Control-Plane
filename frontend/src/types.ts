// All shared TypeScript types used across the portal.
// Backend entity shapes come in as-received; mapped types are the frontend view model.

export type TenantSummary = {
  tenantId: string;
  name: string;
  description?: string | null;
  status: string;
};

export type ApplicationSummary = {
  applicationId: string;
  name: string;
  description?: string;
  tenantId?: string | null;
  riskLevel: string;
  status: string;
  sourceOfTruthMode?: string;
  policyCombiningAlgorithm?: string;
  ownerTeam?: string;
  businessOwner?: string;
  technicalOwner?: string;
};

export type RoleSummary = {
  roleKey: string;
  name: string;
  privileged: boolean;
  riskLevel: string;
  status: string;
  permissions: string[];
  description?: string | null;
};

export type PermissionSummary = {
  id: string;
  permissionKey: string;
  resource: string;
  action: string;
  riskLevel: string;
  status: string;
  description?: string | null;
};

export type RolePermissionSummary = {
  id: string;
  roleKey: string;
  permissionKey: string;
  state: string;
  publishedAt: string | null;
};

export type AssignmentSummary = {
  id?: string;
  subjectEmail: string;
  roleKey: string;
  state: string;
  validUntil: string | null;
  reason?: string | null;
};

export type PolicySummary = {
  policyKey: string;
  permissionKey: string;
  effect: string;
  state: string;
  priority?: number;
  obligations?: string;
  publishedAt?: string | null;
  conditions?: string;
};

export type ReferenceDataSummary = {
  id: string;
  key: string;
  description?: string | null;
  value: string;
  status: string;
  createdAt?: string | null;
  createdBy?: string | null;
  updatedAt?: string | null;
};

export type OidcProviderSummary = {
  id: string;
  providerType: string;
  issuer: string;
  audience: string;
  jwksUri: string;
  allowedAlgorithms: string[];
  subjectType: string;
  subjectClaim: string;
  enabled: boolean;
  createdAt: string | null;
  createdBy: string | null;
  updatedAt: string | null;
};

export type AuditEventSummary = {
  eventId?: string;
  eventType: string;
  applicationId: string;
  actorEmail: string;
  actorRole?: string | null;
  targetSubjectEmail?: string | null;
  timestamp: string;
  oldValue?: string | null;
  newValue?: string | null;
  correlationId?: string | null;
};

export type AuditActivityDay = {
  date: string;
  count: number;
};

export type AuditActivitySummary = {
  total: number;
  days: AuditActivityDay[];
};

export type SimulatorDecision = {
  allowed: boolean;
  decisionId: string;
  denyReason: string | null;
  reason?: {
    matchedRoles: string[];
    matchedPermissions: string[];
    matchedPolicies: string[];
  };
  obligations?: { id: string; value: string | null }[];
};

export type Notification = {
  message: string;
  tone: "success" | "error";
};

// ── Server-owned runtime configuration ───────────────────────────────────────

export type AiFeatureName =
  | "policyAuthoring"
  | "decisionExplainer"
  | "impactAnalysis"
  | "configAdvisor"
  | "accessSearch"
  | "sodAnalysis"
  | "accessCertification"
  | "auditNarrative";

export type AiConfig = {
  enabled: boolean;
  // Runtime provenance, present only when AI is enabled (null otherwise). Never
  // includes secrets — the API key is server-side only.
  provider?: string | null;
  model?: string | null;
  limits?: {
    requestTimeoutSeconds: number;
    maxTokens: number;
    maxPromptChars: number;
    temperature: number;
  } | null;
  features: {
    policyAuthoring: boolean;
    decisionExplainer: boolean;
    impactAnalysis: boolean;
    configAdvisor: boolean;
    accessSearch: boolean;
    sodAnalysis: boolean;
    accessCertification: boolean;
    auditNarrative: boolean;
  };
  // Per-feature sampling temperature, present only when AI is enabled (null otherwise).
  featureTemperatures?: Record<AiFeatureName, number> | null;
};

export type PaginationConfig = {
  defaultPageSize: number;
  maxPageSize: number;
  pageSizeOptions: number[];
};

export type CacheConfig = {
  defaultStaleMs: number;
  volatileStaleMs: number;
  configStaleMs: number;
};

export type UiConfig = {
  aiReportingWindows: number[];
  activityTrendDays: number;
  auditPageSize: number;
};

export type PortalConfig = {
  ai: AiConfig;
  pagination: PaginationConfig;
  cache: CacheConfig;
  ui: UiConfig;
  // Friendly display labels for well-known actor roles, keyed by normalised role id.
  roleLabels: Record<string, string>;
};

// ── Insight / overview view models (platform + per-application scope) ─────────

export type TenantApplicationCount = {
  tenantId: string;
  tenantName: string;
  applicationCount: number;
};

export type PlatformOverview = {
  tenantCount: number;
  applicationCount: number;
  roleCount: number;
  permissionCount: number;
  policyCount: number;
  assignmentCount: number;
  activeAssignmentCount: number;
  applicationsByRisk: Record<string, number>;
  applicationsByTenant: TenantApplicationCount[];
  recentAudit: AuditEventSummary[];
};

export type ApplicationOverview = {
  applicationId: string;
  name: string;
  roleCount: number;
  privilegedRoleCount: number;
  permissionCount: number;
  policyCount: number;
  publishedPolicyCount: number;
  draftPolicyCount: number;
  assignmentCount: number;
  activeAssignmentCount: number;
  roleRiskDistribution: Record<string, number>;
  recentAudit: AuditEventSummary[];
};

export type TenantApplicationSummary = {
  applicationId: string;
  name: string;
  status: string;
  riskLevel: string;
  roleCount: number;
  assignmentCount: number;
  activeAssignmentCount: number;
};

export type TenantRollup = {
  applicationCount: number;
  roleCount: number;
  assignmentCount: number;
  activeAssignmentCount: number;
};

export type TenantDetail = {
  tenant: TenantSummary;
  applications: TenantApplicationSummary[];
  rollup: TenantRollup;
};

export type UserDirectoryEntry = {
  email: string;
  applicationCount: number;
  assignmentCount: number;
  activeCount: number;
  expiredCount: number;
  revokedCount: number;
};

export type UserAccessAssignment = {
  applicationId: string;
  roleKey: string;
  state: string;
  validUntil: string | null;
};

export type UserAccess = {
  email: string;
  assignments: UserAccessAssignment[];
};
