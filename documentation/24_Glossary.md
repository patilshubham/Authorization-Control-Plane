# 24 — Glossary

> Part of the [Documentation Portal](README.md).
> Related: [Project Overview](01_Project_Overview.md) · [Data Model](14_Data_Model_Documentation.md) · [Business Rules](19_Business_Rules.md) · [Security Design](16_Security_Design.md)

---

## Table of Contents

1. [Purpose & How to Use This Glossary](#purpose--how-to-use-this-glossary)
2. [Domain & Governance Terms](#domain--governance-terms)
3. [Runtime & Decision Terms](#runtime--decision-terms)
4. [Deny Reason Codes](#deny-reason-codes)
5. [Delegated-Admin Capabilities & Roles](#delegated-admin-capabilities--roles)
6. [Security & Identity Terms](#security--identity-terms)
7. [AI Terms](#ai-terms)
8. [Technical & Platform Terms](#technical--platform-terms)
9. [Status & State Vocabularies](#status--state-vocabularies)
10. [Runtime & Caller Error Codes](#runtime--caller-error-codes)
11. [Acronyms](#acronyms)
12. [Cross-References](#cross-references)

---

## Purpose & How to Use This Glossary

This glossary is the single, grounded reference for the vocabulary used throughout the documentation
and the codebase. Every definition here is tied to what the implementation actually does, so a reader
with no prior context can look up any unfamiliar term, code, or acronym encountered in another
document.

How to use it:

- Terms are grouped by theme; within each group they read top to bottom by relatedness.
- **Enumerated values** (status vocabularies, error codes) are listed exhaustively with their source
  file, because other documents refer to these strings verbatim.
- Where a concept has a dedicated document, the entry links to it rather than duplicating detail.
- Code identifiers appear in `monospace`; the authoritative source file is linked or named.

## Domain & Governance Terms

These are the entities and concepts an administrator models in the **control plane**.

| Term | Definition |
|------|------------|
| **Control plane** | The governance side of the system: modeling and administering access. Exposed through the admin portal and the `/v1/admin/*` APIs. |
| **Runtime plane** | The enforcement side: answering `POST /v1/authorize` (and `/v1/authorize/batch`) at request time for protected applications. |
| **Tenant** | Top-level ownership boundary (`TenantEntity`). Owns applications. Managed via `/v1/admin/tenants`. |
| **Application** | A protected system registered under a tenant (`ApplicationEntity`). Carries a `SourceOfTruthMode` and a `PolicyCombiningAlgorithm`; only an `ACTIVE` application resolves at runtime. |
| **Role** | A named bundle of access within an application (`RoleEntity`). May be **privileged** (`Privileged = true`), which forces time-boxed assignments. |
| **Permission** | A `resource` + `action` pair an application recognizes (`PermissionEntity`). Only an `ACTIVE` permission resolves at runtime. |
| **Role–permission mapping** | Links a role to a permission (`RolePermissionEntity`). Must be `PUBLISHED` before it grants any runtime access. |
| **Assignment** | Grants a subject a role within an application (`AssignmentEntity`), optionally time-boxed with `ValidFrom`/`ValidUntil`. |
| **Break-glass** | An emergency, time-boxed assignment (1–24 hours) that auto-expires and raises a high-visibility audit event (`BREAK_GLASS_ACTIVATED`). |
| **Policy** | An optional, additive ABAC guardrail (`PolicyEntity`) with an **effect** (`ALLOW`/`DENY`), a **priority**, JSON **conditions**, and optional **obligations**. Only `PUBLISHED` policies affect decisions. |
| **Obligation** | An advisory instruction returned with an allow decision that the calling application must enforce itself (e.g. `require_mfa`). The control plane emits obligations; it does not enforce them. |
| **Reference data** | Named, application-scoped JSON lookup documents (`ReferenceDataEntity`) that policies can reference through the `reference.*` namespace. |
| **SoD rule** | A Separation-of-Duties rule (`SodRuleEntity`) defining two permission **matchers** (`MatcherA`/`MatcherB`, each `{ permissionKey?, resource?, action? }`) that a subject must not hold together, plus a `Severity`. |
| **Review campaign** | An access-recertification workflow (`ReviewCampaignEntity`). Activating it snapshots current access into **review items** (`ReviewItemEntity`) that reviewers decide, then finalizing applies the `REVOKE` outcomes. |
| **Delegated admin** | A portal user whose administrative reach is scoped by their platform, application, or tenant role. |
| **Capability** | A single gated admin ability (`DelegatedAdminCapability`), e.g. `ManageRoles`. See [Delegated-Admin Capabilities & Roles](#delegated-admin-capabilities--roles). |
| **Scope** | The breadth over which a delegated admin's capabilities apply: platform-wide, a single application, or all applications in a tenant. |
| **Source-of-truth mode** | Declares who owns an application's access data (`SourceOfTruthMode`): platform-owned, external-read, dual-write, or external-owned. |
| **Actor** | The authenticated principal that performed a governance mutation, recorded on every audit event (`User.Identity.Name`, falling back to `local-admin`). |

## Runtime & Decision Terms

These describe how a runtime authorization request is evaluated and answered.

| Term | Definition |
|------|------------|
| **Decision** | The result of an authorization evaluation (`AuthorizeResponse` at the API; persisted as a `DecisionEntity`). |
| **Decision ID** | A unique identifier assigned to each decision, returned to the caller and recorded for analytics/audit. |
| **RBAC baseline** | The rule that, when a permission has **no** published policies, an active assignment plus a published role→permission mapping is sufficient to allow. Policies are an additive layer *on top of* this baseline. |
| **ABAC** | Attribute-Based Access Control: policy conditions evaluated against attributes to refine the RBAC baseline. |
| **Deny reason** | A structured code explaining a denial (see [Deny Reason Codes](#deny-reason-codes)). |
| **Effect** | A policy's outcome contribution: `ALLOW` or `DENY`. |
| **Priority** | An integer used by the combining algorithm to order matched policies. |
| **Policy combining algorithm** | How multiple matched policies resolve to one outcome: `deny-overrides` (default), `allow-overrides`, or `first-applicable`. Configured per application. |
| **Match type** | How the nodes in a condition group combine: `all` (AND), `any` (OR), or `none` (NOT any). Groups nest recursively. |
| **Operator** | The comparison a leaf condition applies (e.g. `equals`, `in`, `greaterThan`). The full set is defined in [PolicyOperators.cs](../backend/src/Authorization.Infrastructure/RuntimeAuthorization/PolicyOperators.cs); see [Business Rules](19_Business_Rules.md). |
| **Context** | Caller-supplied attributes (≤ 32 KB JSON) evaluated by policy conditions, referenced through `context.*`. |
| **`context.*` attributes** | Values taken from the request's `context` object. |
| **`assignment.*` attributes** | Attributes stored on the subject's assignment (`AssignmentAttributeEntity`). |
| **`system.*` attributes** | A computed evaluation-clock namespace (current time), built once per decision so conditions can reason about time without the caller supplying it. |
| **`reference.*` attributes** | Values resolved from the application's reference-data documents; queried only when a policy actually references them. |
| **Batch authorize** | Up to **50** checks in one call (`POST /v1/authorize/batch`) sharing one authenticated subject and a base context; each check's context is merged onto the base (per-check key wins). |
| **Simulator** | An admin dry-run of a decision (`POST /v1/admin/simulator/authorize`) using the **same engine** as runtime, without recording an enforcement decision or requiring a runtime credential. |

## Deny Reason Codes

The engine ([EfAuthorizationPolicyEngine.cs](../backend/src/Authorization.Infrastructure/RuntimeAuthorization/EfAuthorizationPolicyEngine.cs))
returns exactly one of these codes on a denial, in the order the ladder is evaluated. See
[Business Rules](19_Business_Rules.md) for the full decision semantics.

| Deny reason | Meaning |
|-------------|---------|
| `APPLICATION_NOT_FOUND` | No application matches `applicationId` with `Status = ACTIVE`. |
| `PERMISSION_NOT_FOUND` | No `ACTIVE` permission matches the requested `resource` + `action`. |
| `ASSIGNMENT_REVOKED` | The subject's only assignments were revoked (surfaced when no active assignment remains). |
| `ASSIGNMENT_EXPIRED` | The subject's only assignments are past their validity window. |
| `NO_ACTIVE_ASSIGNMENT` | The subject has no assignment active for this application right now. |
| `PERMISSION_NOT_GRANTED` | The subject holds roles, but none has a `PUBLISHED` mapping to the requested permission. |
| `EXPLICIT_DENY` | A matched `DENY` policy is the deciding policy under the combining algorithm. |
| `MISSING_CONTEXT` | A referenced attribute could not be resolved and no allow policy matched — fail-closed. |
| `DENY_BY_DEFAULT` | Allow policies exist for the permission but none matched — fail-closed. |

## Delegated-Admin Capabilities & Roles

**Capabilities** (`DelegatedAdminCapability`) are the atomic admin abilities each governance
operation requires:

| Capability | Gates |
|------------|-------|
| `ManageApplication` | Application parameters and lifecycle status. |
| `ManageRoles` | Creating/updating/retiring roles. |
| `ManagePermissions` | Creating/updating/retiring permissions. |
| `MapRolePermission` | Drafting and publishing role→permission mappings. |
| `ManagePolicies` | Authoring and publishing ABAC policies. |
| `AssignRoles` | Granting, revoking, extending, and break-glass assignments. |
| `ViewAudit` | Reading the audit trail. |
| `ReadOnlyView` | Read access to governed configuration and analytics. |

**Roles** (`DelegatedAdminRoles`) bundle capabilities at a given scope; the portal derives the
current user's capability set from their role claims:

| Role | Scope & reach |
|------|---------------|
| `PlatformSuperAdmin` | Full capabilities across the whole platform; may manage tenants and applications. |
| `PlatformReadOnlyViewer` | Read-only (`ViewAudit` + `ReadOnlyView`) across all applications. |
| `ApplicationAdmin` | Full capabilities for a single application. |
| `ReadOnlyViewer` | Read-only for a single application. |
| `TenantAdmin` | Full capabilities across every application owned by a tenant. |
| `TenantReadOnlyViewer` | Read-only across a tenant's applications. |

## Security & Identity Terms

| Term | Definition |
|------|------------|
| **OIDC** | OpenID Connect. Used both for portal admin login (via Keycloak) and to validate per-application runtime callers. |
| **AdminJwt** | The **only** wired ASP.NET authentication scheme (`ApiAuthenticationSchemes.AdminJwt`); a JWT-bearer handler that protects `/v1/admin/*` and `/v1/config`. |
| **Runtime caller authentication** | How `/v1/authorize*` is secured: the controller extracts the bearer itself and validates it with `RuntimeCallerAuthenticator` against the requested application's OIDC provider(s). There is **no** `[Authorize]` attribute or middleware scheme on this path. |
| **`RuntimeClientCredentials`** | Only a *string constant* in `ApiAuthenticationSchemes` — it is **not** registered as an authentication scheme and no controller references it. The `RuntimeApi` policy, `RuntimeClientCredentialValidator`, and the `RuntimeClients` config section are a **legacy static-secret path retained only for unit tests**, not part of the live pipeline. See [Security Design](16_Security_Design.md). |
| **PKCE** | Proof Key for Code Exchange (S256). Used by the portal's authorization-code login. |
| **JWKS** | JSON Web Key Set; the source of token signing keys, resolved per OIDC provider. |
| **Subject** | The identity being authorized. The stored `subject_type` is one of `USER`, `GROUP`, `SERVICE_ACCOUNT`, `EXTERNAL_USER`, `TENANT`, `APPLICATION` (default `USER`). At runtime the subject is derived **authoritatively from the verified token** (typically `USER` or `SERVICE_ACCOUNT`); a conflicting body subject is rejected with `403 SUBJECT_MISMATCH`. |
| **OIDC provider subject type** | An OIDC provider is registered as `USER` or `SERVICE_ACCOUNT`, controlling which token claim (`SubjectClaim`, default `sub`) yields the subject. |
| **Claim types** | `acp_platform_role`, `acp_app_role` (`{appId}:{role}`), and `acp_tenant_role` (`{tenantId}:{role}`) — the delegated-admin role claims (`DelegatedAdminClaimTypes`). |
| **Correlation ID** | The `X-Correlation-ID` value (≤ 128 chars, charset `A-Za-z0-9-_.:`) that links a request's logs, traces, audit events, and decisions; echoed on the response. |

## AI Terms

| Term | Definition |
|------|------------|
| **Advisory-only** | The defining property of the AI layer: it explains, summarizes, and drafts, but never participates in an authorization decision. |
| **Fact builder** | A deterministic API-layer component (e.g. `DecisionDiagnosticsBuilder`) that computes real facts from the database for the model to narrate. |
| **Grounding** | Constraining AI output to the application's real vocabulary and facts — no invented fields, roles, or SQL. |
| **Pseudonymization** | Replacing subject PII (emails/identities) with pseudonymous IDs before any prompt is built (`SubjectPseudonymizer`). |
| **AiAvailability** | The immutable startup snapshot of effective AI state (`enabled`, `provider`, per-feature flags). The single gate every AI endpoint consults; `AiAvailability.Disabled` when off or misconfigured. |
| **Provider** | The chat backend selected by `Ai:Provider`: `AzureOpenAI` (default), `OpenAI` (OpenAI-compatible, e.g. GitHub Models), or the deterministic `Fake` stub. Default chat deployment: `gpt-4o-mini`. |
| **AI feature key** | One of the eight independently gated features (`AiFeatureOptions`): `PolicyAuthoring`, `DecisionExplainer`, `ImpactAnalysis`, `ConfigAdvisor`, `AccessSearch`, `SodAnalysis`, `AccessCertification`, `AuditNarrative`. |
| **Invocation / prompt log** | Append-only records of AI use (`AiInvocationEntity`, `AiPromptLogEntity`), with subjects pseudonymized, feeding the AI Usage page. |

Full behavior: [AI Features](15_AI_Features.md).

## Technical & Platform Terms

| Term | Definition |
|------|------------|
| **Options pattern** | Strongly-typed, validated configuration binding (e.g. `OidcOptions`, `AiOptions`). See [Configuration](20_Configuration.md). |
| **Middleware** | A pipeline component; here specifically `CorrelationIdMiddleware` and `ExceptionHandlingMiddleware`. |
| **DbContext** | The EF Core unit-of-work over the `authz` schema (`AuthorizationDbContext`). |
| **Migration** | A versioned EF Core schema change; applied automatically in development at startup. |
| **Seeder** | `LocalDevelopmentSeeder`, which provisions demo tenants, applications, roles, users, and policies for local development. |
| **Decision outbox** | `InProcessDecisionOutbox` (`IDecisionRecorder`): an **in-memory**, bounded (10,000) channel that is intentionally **lossy** under sustained backpressure (`DropWrite`, drops logged with a running count) so runtime latency is never coupled to persistence throughput. It is *not* a durable database outbox table. |
| **Decision persistence worker** | `DecisionPersistenceWorker`, the background service that drains the outbox and writes decisions to the `decisions` table (with bounded requeue/retry). |
| **Optimistic concurrency** | Every audited entity carries an integer `version` column configured as an EF concurrency token; a stale update fails with `DbUpdateConcurrencyException` rather than overwriting. |
| **Typed HttpClient** | The SDK's pooled, resilient HTTP client (`AuthorizationClient`) protected applications use to call the runtime API. |
| **Canonical error envelope** | The uniform error shape `{ error: { code, message }, correlationId }` (`ApiErrorEnvelope`). See [Error Handling](17_Error_Handling.md). |
| **Health checks** | `/health/live` (liveness — no checks) and `/health/ready` (database readiness, tagged `ReadyTag`). |
| **OpenTelemetry** | Tracing with the `Authorization.RuntimeAuthorization` activity source; each decision emits one span tagged with application, resource, action, allowed, deny reason, and duration. |

## Status & State Vocabularies

Defined in [GovernanceVocabulary.cs](../backend/src/Authorization.Api/Governance/GovernanceVocabulary.cs):

| Vocabulary | Values |
|------------|--------|
| `GovernanceStatus` (applications, roles, permissions, reference data, SoD rules) | `ACTIVE`, `DISABLED`, `ARCHIVED`, `DEPRECATED` |
| `WorkflowState` (policies, role–permission mappings, assignments) | `DRAFT`, `PUBLISHED`, `REVOKED`, `ACTIVE` |
| `RiskLevel` (applications, roles, permissions; also SoD `Severity`) | `LOW`, `MEDIUM`, `HIGH`, `CRITICAL` |
| `AssignmentDisplayStatus` (derived, not stored) | `ACTIVE`, `EXPIRED`, `REVOKED` — resolved from stored state, expiry, and revocation timestamp |

Defined elsewhere and enforced by `authz` check constraints or validation attributes:

| Vocabulary | Values | Source |
|------------|--------|--------|
| Assignment `subject_type` | `USER`, `GROUP`, `SERVICE_ACCOUNT`, `EXTERNAL_USER`, `TENANT`, `APPLICATION` | `ck_assignments_subject_type` ([AuthorizationDbContext.cs](../backend/src/Authorization.Infrastructure/Persistence/AuthorizationDbContext.cs)) |
| `PolicyCombiningAlgorithm` | `deny-overrides` (default), `allow-overrides`, `first-applicable` | `[AllowedValues]` in [GovernanceRequests.cs](../backend/src/Authorization.Api/Contracts/GovernanceRequests.cs) |
| `SourceOfTruthMode` | `PLATFORM_OWNED`, `EXTERNAL_READ`, `DUAL_WRITE`, `EXTERNAL_OWNED` | `[AllowedValues]` in [GovernanceRequests.cs](../backend/src/Authorization.Api/Contracts/GovernanceRequests.cs) |
| OIDC provider `SubjectType` | `USER`, `SERVICE_ACCOUNT` | `[AllowedValues]` in [GovernanceRequests.cs](../backend/src/Authorization.Api/Contracts/GovernanceRequests.cs) |
| Review campaign `status` | `DRAFT`, `ACTIVE`, `CLOSED` | `ck_review_campaigns_status` ([AuthorizationDbContext.cs](../backend/src/Authorization.Infrastructure/Persistence/AuthorizationDbContext.cs)) |
| Review item `decision` | `PENDING` (default), `KEEP`, `REVOKE`, `NEEDS_INFO` | [ReviewCampaignsController.cs](../backend/src/Authorization.Api/Controllers/Governance/ReviewCampaignsController.cs) |

See [Data Model](14_Data_Model_Documentation.md) for the entities these vocabularies constrain.

## Runtime & Caller Error Codes

The runtime endpoints return these codes in the [canonical error envelope](#technical--platform-terms).
Governance (admin) error codes are catalogued in [Error Handling](17_Error_Handling.md).

| Code | HTTP | Meaning |
|------|------|---------|
| `SUBJECT_MISMATCH` | 403 | A body `subject.email` conflicts with the verified token subject. |
| `REQUEST_TOO_LARGE` | 413 | The request body exceeds 256 KB. |
| `BATCH_TOO_LARGE` | 413 | A batch has more than 50 checks. |
| `CONTEXT_TOO_LARGE` | 422 | The context JSON exceeds 32 KB (per-check in a batch, that single check is denied with this code). |
| `CALLER_UNAUTHENTICATED` | 401 | No bearer, no OIDC provider for the app, or the token never validated. |
| `CALLER_APPLICATION_MISMATCH` | 403 | The token validated but failed the provider's required-claim binding for the application. |
| `CALLER_SUBJECT_CLAIM_MISSING` | 403 | The token matched but lacks the provider's configured subject claim (wrong token type). |

Input limits referenced above: batch ≤ **50** checks, body ≤ **256 KB**, context ≤ **32 KB**,
AI prompt ≤ **4000** chars, correlation ID ≤ **128** chars.

## Acronyms

| Acronym | Expansion |
|---------|-----------|
| ACP | Authorization Control Plane |
| RBAC | Role-Based Access Control |
| ABAC | Attribute-Based Access Control |
| SoD | Separation of Duties |
| OIDC | OpenID Connect |
| JWT | JSON Web Token |
| JWKS | JSON Web Key Set |
| PKCE | Proof Key for Code Exchange |
| PII | Personally Identifiable Information |
| CORS | Cross-Origin Resource Sharing |
| SPA | Single-Page Application |
| DI | Dependency Injection |
| DTO | Data Transfer Object |
| EF | Entity Framework |
| ERD | Entity-Relationship Diagram |
| MVP | Minimum Viable Product |
| KPI | Key Performance Indicator |

## Cross-References

- Where these terms originate: [Data Model](14_Data_Model_Documentation.md), [Business Rules](19_Business_Rules.md)
- Security terms in context: [Security Design](16_Security_Design.md)
- AI terms in context: [AI Features](15_AI_Features.md)
- Error codes and status mapping: [Error Handling](17_Error_Handling.md)
- Concepts in the enforcement code path: [Code Walkthrough](23_Code_Walkthrough.md)
- Options and configuration keys: [Configuration](20_Configuration.md)
- First introduction of the concepts: [Project Overview](01_Project_Overview.md)
