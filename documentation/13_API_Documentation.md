# 13 — API Documentation

> Part of the [Documentation Portal](README.md).
> Related: [Module Design](12_Module_Design.md) · [Feature Documentation](08_Feature_Documentation.md) · [Error Handling](17_Error_Handling.md) · [Security Design](16_Security_Design.md)

---

## Table of Contents

1. [Purpose & Conventions](#purpose--conventions)
2. [Authentication & Authorization](#authentication--authorization)
3. [Common Conventions](#common-conventions)
4. [Runtime Authorization API](#runtime-authorization-api)
5. [Config API](#config-api)
6. [Governance APIs](#governance-apis)
7. [Insight & Analytics APIs](#insight--analytics-apis)
8. [AI APIs](#ai-apis)
9. [Common Response Shapes](#common-response-shapes)
10. [Status Codes](#status-codes)
11. [Cross-References](#cross-references)

---

## Purpose & Conventions

This is the endpoint reference. Routes, DTOs, and status codes are taken from the controllers in
[Authorization.Api/Controllers](../backend/src/Authorization.Api/Controllers/) and contracts in
[Authorization.Api/Contracts](../backend/src/Authorization.Api/Contracts/).

- **Admin base:** `/v1/admin` (portal, `AdminJwt` scheme).
- **Config base:** `/v1/config` (portal, `AdminJwt` scheme).
- **Runtime base:** `/v1` — `/v1/authorize` and `/v1/authorize/batch` (machine callers).
- **Content type:** requests and responses are `application/json` (except CSV on assignment export/import).
- **Correlation:** clients send `X-Correlation-ID`; it is echoed on the response and included in the error envelope. If omitted, the API generates one.
- **Versioning:** the `v1` path segment is the API version; there is no header-based negotiation.
- **Errors:** all errors use the canonical envelope (see [Error Handling](17_Error_Handling.md)).

## Authentication & Authorization

### Schemes

| Surface | Scheme | Token source | How it is enforced |
|---------|--------|--------------|--------------------|
| Portal admin APIs (`/v1/admin`, `/v1/config`) | `AdminJwt` (JWT Bearer) | Keycloak (realm `authorization-local`) | `[Authorize(Policy = AdminApi)]` on every admin/config controller |
| Runtime APIs (`/v1/authorize`, `/v1/authorize/batch`) | Per-application bearer token | The target application's configured OIDC provider | The controller has **no** `[Authorize]` attribute; it extracts the bearer token itself and validates it with `RuntimeCallerAuthenticator` against the app's OIDC provider |

### Delegated-admin policy model

Every admin controller carries a controller-level baseline policy
`[Authorize(Policy = DelegatedAdminPolicyNames.AdminApi)]`. Individual mutating (and some read)
actions **override** this baseline with a finer capability policy — for example `PlatformAdmin`,
`ManageApplication`, `ManageRoles`, `ManagePermissions`, `MapRolePermission`, `AssignRoles`,
`ManagePolicies`, or `ReadOnlyView`. Where an action has **no** override in the tables below it is
gated only by the `AdminApi` baseline (results are still scoped to what the caller may see).

Capability-to-role mapping is in [Security Design](16_Security_Design.md).

## Common Conventions

### Pagination

List endpoints that support paging accept optional `page` and `pageSize` query parameters and return
[`PagedResult<T>`](../backend/src/Authorization.Api/Contracts/Pagination.cs):

```json
{ "items": [ /* current page */ ], "page": 1, "pageSize": 25, "total": 128 }
```

- Paging is **opt-in**: if neither `page` nor `pageSize` is supplied, the full (unpaged) collection is returned.
- `page` is clamped to at least 1; `pageSize` is clamped to `1..200`; the default page size is `25`.

### Filtering

Filterable list endpoints (e.g. applications, assignments) accept a free-text `q` parameter plus
resource-specific filters documented per endpoint below.

### Error envelope

Every non-2xx response uses `ApiErrorEnvelope`:

```json
{ "error": { "code": "TENANT_NOT_FOUND", "message": "…", "details": { } }, "correlationId": "…" }
```

`correlationId` echoes the request's `X-Correlation-ID` (or a generated value). Codes and their
mapping to HTTP status are catalogued in [Error Handling](17_Error_Handling.md).

### Concurrency

Governance resources use an integer `version` optimistic-concurrency token; a stale update is
rejected with `409` (see [Data Model](14_Data_Model_Documentation.md)).

## Runtime Authorization API

Implemented by [RuntimeAuthorizationController.cs](../backend/src/Authorization.Api/Controllers/RuntimeAuthorizationController.cs).
The controller has no `[Authorize]` attribute; it extracts the bearer token from the `Authorization`
header and validates it against the target application's OIDC provider via `RuntimeCallerAuthenticator`.
An invalid or missing token returns `401`; a token issued for a different application returns `403`.

### POST `/v1/authorize`

Evaluate a single decision.

**Request** (`AuthorizeApiRequest`):

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `applicationId` | string | ✓ | Target application |
| `subject` | `{ type, email? }` | ✓ | `type` is a subject-type string (e.g. `USER`, `SERVICE_ACCOUNT`). The **effective subject is always taken from the verified token**; `email` is optional and, if supplied, must equal the token subject or the call is rejected `403 SUBJECT_MISMATCH`. |
| `claims` | object | | Optional caller claims |
| `resource` | `{ type, id? }` | ✓ | `type` + optional `id` |
| `action` | string | ✓ | e.g. `publish` |
| `context` | object | | ≤ 32 KB serialized |

```json
POST /v1/authorize
Authorization: Bearer <runtime token>
{
  "applicationId": "pricing-management",
  "subject": { "type": "SERVICE_ACCOUNT" },
  "resource": { "type": "price", "id": "price-1" },
  "action": "publish",
  "context": { "status": "READY_TO_PUBLISH" }
}
```

**Response** (`AuthorizeResponse`, 200):

```json
{
  "allowed": true,
  "decisionId": "3f2a...",
  "denyReason": null,
  "reason": { "matchedRoles": ["pricing-lead"], "matchedPermissions": ["price.publish"], "matchedPolicies": ["allow-publish-ready"] },
  "obligations": []
}
```

**Limits & errors:** body > 256 KB → 413 `REQUEST_TOO_LARGE`; context > 32 KB → 422
`CONTEXT_TOO_LARGE`; caller invalid → 401; application mismatch / subject claim missing → 403; body
subject conflicts with token → 403 `SUBJECT_MISMATCH`.

### POST `/v1/authorize/batch`

Evaluate up to **50** checks sharing one subject/base-context.

**Request** (`BatchAuthorizeApiRequest`): `applicationId`, `subject`, optional `claims`, a shared
`context?`, and `checks: [{ resource, action, context? }]`.

```json
POST /v1/authorize/batch
{
  "applicationId": "pricing-management",
  "subject": { "type": "SERVICE_ACCOUNT" },
  "context": { "status": "READY_TO_PUBLISH" },
  "checks": [
    { "resource": { "type": "price", "id": "price-1" }, "action": "publish" },
    { "resource": { "type": "price", "id": "price-2" }, "action": "publish", "context": { "status": "DRAFT" } }
  ]
}
```

**Response** (`BatchAuthorizeResponse`): `{ "results": [AuthorizeResponse, ...] }`, order preserved.

**Errors:** > 50 checks → 413 `BATCH_TOO_LARGE`. Oversized per-check context denies that check with
`CONTEXT_TOO_LARGE` instead of failing the batch. Per-check context overrides base context.

## Config API

### GET `/v1/config`

Implemented by [ConfigController.cs](../backend/src/Authorization.Api/Controllers/ConfigController.cs)
(`AdminApi` policy). Returns `PortalConfigResponse`:

```json
{
  "ai": {
    "enabled": false,
    "provider": null,
    "model": null,
    "limits": null,
    "features": { "policyAuthoring": false, "decisionExplainer": false, "impactAnalysis": false,
                  "configAdvisor": false, "accessSearch": false, "sodAnalysis": false,
                  "accessCertification": false, "auditNarrative": false },
    "featureTemperatures": null
  },
  "pagination": { "defaultPageSize": 25, "maxPageSize": 200, "pageSizeOptions": [10,25,50,100] },
  "cache": { "defaultStaleMs": 30000, "volatileStaleMs": 10000, "configStaleMs": 300000 },
  "ui": { "aiReportingWindows": [7,30,90], "activityTrendDays": 14, "auditPageSize": 200 },
  "roleLabels": { "platformsuperadmin": "Platform Super Admin", "platformreadonlyviewer": "Platform Read-only Viewer",
                  "applicationadmin": "Application Admin", "readonlyviewer": "Read-only Viewer" }
}
```

> `roleLabels` keys are the **lower-cased** role identifiers (`platformsuperadmin`,
> `platformreadonlyviewer`, `applicationadmin`, `readonlyviewer`).
> When AI is enabled, `provider` is the configured provider and `limits` is
> `{ requestTimeoutSeconds, maxTokens, maxPromptChars, temperature }`; `model` is the Azure OpenAI
> chat deployment only for a live (non-`fake`) provider. The API key is never included.
> The example values reflect defaults; actual values come from [PortalOptions](../backend/src/Authorization.Api/Configuration/PortalOptions.cs)
> and [AiOptions](../backend/src/Authorization.Ai/AiOptions.cs).

## Governance APIs

All under `/v1/admin`, guarded by delegated-admin capability policies. Full behavior in
[Feature Documentation](08_Feature_Documentation.md); capability mapping in [Security Design](16_Security_Design.md).

### Tenants — [TenantsController.cs](../backend/src/Authorization.Api/Controllers/Governance/TenantsController.cs)

| Method | Route | Policy | Purpose |
|--------|-------|--------|---------|
| GET | `/tenants` | AdminApi (baseline) | List (scoped to accessible tenants) |
| GET | `/tenants/{tenantId}` | AdminApi (baseline) | Detail + app rollup |
| POST | `/tenants` | PlatformAdmin | Create |
| PUT | `/tenants/{tenantId}` | PlatformAdmin | Update |
| DELETE | `/tenants/{tenantId}` | PlatformAdmin | Delete |

### Applications — [ApplicationsController.cs](../backend/src/Authorization.Api/Controllers/Governance/ApplicationsController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications` (filters: `tenantId`, `status`, `riskLevel`, `q`; paged via `page`/`pageSize`) | AdminApi (baseline) |
| POST | `/applications` | PlatformAdmin |
| PUT | `/applications/{applicationId}` | ManageApplication |
| POST | `/applications/{applicationId}/activate` \| `/disable` \| `/archive` | ManageApplication |

### Roles — [RolesController.cs](../backend/src/Authorization.Api/Controllers/Governance/RolesController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/roles` | ReadOnlyView |
| POST | `/applications/{id}/roles` | ManageRoles |
| PUT/DELETE | `/applications/{id}/roles/{roleKey}` | ManageRoles |
| POST | `.../roles/{roleKey}/activate` \| `/disable` \| `/archive` | ManageRoles |

### Permissions — [PermissionsController.cs](../backend/src/Authorization.Api/Controllers/Governance/PermissionsController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/permissions` | ReadOnlyView |
| POST | `/applications/{id}/permissions` | ManagePermissions |
| PUT/DELETE | `.../permissions/{permissionKey}` | ManagePermissions |
| POST | `.../permissions/{permissionKey}/activate` \| `/disable` \| `/archive` | ManagePermissions |

### Role–Permission Mappings — [RolePermissionsController.cs](../backend/src/Authorization.Api/Controllers/Governance/RolePermissionsController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/role-permissions` | ReadOnlyView |
| POST | `/applications/{id}/role-permissions` (optional publish) | MapRolePermission |
| POST | `.../role-permissions/{rolePermissionId}/publish` | MapRolePermission |
| DELETE | `.../role-permissions/{rolePermissionId}` | MapRolePermission |

### Assignments — [AssignmentsController.cs](../backend/src/Authorization.Api/Controllers/Governance/AssignmentsController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/assignments` (state/expiry/`q` filters) | ReadOnlyView |
| GET | `.../assignments/summary` | ReadOnlyView |
| POST | `.../assignments` | AssignRoles |
| POST | `.../assignments/break-glass` (1–24h) | AssignRoles |
| PUT | `.../assignments/{id}` | AssignRoles |
| POST | `.../assignments/{id}/revoke` \| `/extend` | AssignRoles |
| POST | `.../assignments/{id}/attributes` (attach ABAC attribute) | AssignRoles |
| GET | `.../assignments/export` (CSV) | ReadOnlyView |
| POST | `.../assignments/import` (dry-run supported) | AssignRoles |

### Policies — [PoliciesController.cs](../backend/src/Authorization.Api/Controllers/Governance/PoliciesController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/policies` | ReadOnlyView |
| POST | `/applications/{id}/policies` | ManagePolicies |
| PUT | `.../policies/{policyKey}` (draft only) | ManagePolicies |
| POST | `.../policies/{policyKey}/publish` | ManagePolicies |
| GET | `.../policies/{policyKey}/history` | ReadOnlyView |
| DELETE | `.../policies/{policyKey}` | ManagePolicies |

### OIDC Providers — [OidcProvidersController.cs](../backend/src/Authorization.Api/Controllers/Governance/OidcProvidersController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/oidc-providers` | ReadOnlyView |
| POST | `/applications/{id}/oidc-providers` | ManageApplication |
| PUT | `.../oidc-providers/{providerId}` (forbids `none`) | ManageApplication |
| POST | `.../oidc-providers/validate` | ManageApplication |

### Reference Data — [ReferenceDataController.cs](../backend/src/Authorization.Api/Controllers/Governance/ReferenceDataController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/reference-data` | ReadOnlyView |
| POST | `/applications/{id}/reference-data` | ManagePolicies |
| PUT | `.../reference-data/{key}` | ManagePolicies |
| DELETE | `.../reference-data/{key}` (soft → ARCHIVED) | ManagePolicies |

### Review Campaigns — [ReviewCampaignsController.cs](../backend/src/Authorization.Api/Controllers/Governance/ReviewCampaignsController.cs)

| Method | Route | Policy |
|--------|-------|--------|
| GET | `/applications/{id}/review-campaigns` | ReadOnlyView |
| GET | `.../review-campaigns/{campaignId}` | ReadOnlyView |
| POST | `.../review-campaigns` (DRAFT) | AssignRoles |
| POST | `.../review-campaigns/{campaignId}/activate` | AssignRoles |
| POST | `.../review-campaigns/{campaignId}/items/{itemId}/decision` | AssignRoles |
| POST | `.../review-campaigns/{campaignId}/decisions` (bulk) | AssignRoles |
| POST | `.../review-campaigns/{campaignId}/finalize` | AssignRoles |

## Insight & Analytics APIs

All are read-only (except the POST simulator) and hosted under `/v1/admin`.

| Method | Route | Controller | Policy |
|--------|-------|------------|--------|
| GET | `/v1/admin/audit-events` (paged, `q`, category) | GovernanceInsightsController | AdminApi (baseline) |
| GET | `/v1/admin/audit-events/summary` (heatmap) | GovernanceInsightsController | AdminApi (baseline) |
| POST | `/v1/admin/simulator/authorize` | GovernanceInsightsController | AdminApi (baseline) |
| GET | `/v1/admin/users`, `/v1/admin/users/{email}` | GovernanceInsightsController | AdminApi (baseline) |
| GET | `/v1/admin/overview` | GovernanceInsightsController | AdminApi (baseline) |
| GET | `/v1/admin/applications/{id}/overview` | ApplicationsController | ReadOnlyView |
| GET | `/v1/admin/applications/{id}/decisions/analytics` | DecisionAnalyticsController | ReadOnlyView |
| GET | `.../insights/config-findings` | ApplicationInsightsController | AdminApi (baseline) |
| GET | `.../insights/sod-rules`, `.../insights/sod-violations` | ApplicationInsightsController | AdminApi (baseline) |

## AI APIs

Available only when the corresponding feature is enabled. See [AI Features](15_AI_Features.md).
App-scoped routes below (shown as `.../ai/…`) hang off `/v1/admin/applications/{applicationId}/ai`;
platform-scoped routes hang off `/v1/admin/ai`. All AI controllers carry the `AdminApi` baseline policy.

| Method | Route | Feature |
|--------|-------|---------|
| POST | `.../ai/policy-draft` | policyAuthoring |
| POST | `.../ai/explain-decision` | decisionExplainer |
| POST | `.../ai/impact-analysis` | impactAnalysis |
| GET | `.../ai/advisor/findings` | configAdvisor |
| POST | `.../ai/advisor/summarize` | configAdvisor |
| POST | `.../ai/access-search` (app) | accessSearch |
| POST | `/v1/admin/ai/access-search` (platform) | accessSearch |
| POST | `/v1/admin/ai/access-review/summarize` | accessCertification |
| POST | `/v1/admin/ai/audit/narrative` | auditNarrative |
| GET | `.../ai/sod/rules`, `.../ai/sod/violations` | sodAnalysis |
| POST | `.../ai/sod/rules/draft` | sodAnalysis |
| POST | `.../ai/sod/rules`, DELETE `.../ai/sod/rules/{ruleKey}` | sodAnalysis |
| GET | `/v1/admin/ai/usage`, `/v1/admin/ai/prompt-logs` | (telemetry) |

## Common Response Shapes

- **Pagination:** `PagedResult<T>` = `{ items, page, pageSize, total }` ([Pagination.cs](../backend/src/Authorization.Api/Contracts/Pagination.cs)); `total` is the row count across all pages. Paging is opt-in (see [Common Conventions](#common-conventions)).
- **Errors:** `ApiErrorEnvelope` = `{ error: { code, message, details? }, correlationId }`. Model-binding/validation failures use code `VALIDATION_FAILED` (400); governance semantic validation uses `VALIDATION_ERROR` (422); not-found uses resource-specific codes such as `TENANT_NOT_FOUND` / `APPLICATION_NOT_FOUND`.
- **Decision:** `AuthorizeResponse` = `{ allowed, decisionId, denyReason?, reason: { matchedRoles, matchedPermissions, matchedPolicies }, obligations: [{ id, value? }] }`. Batch responses wrap these in `{ results: [...] }`.

## Status Codes

| Code | Meaning in this API |
|------|---------------------|
| 200 | Success |
| 201 | Created (governance POST) |
| 204 | No content (deletes) |
| 400 | Malformed request |
| 401 | Runtime caller unauthenticated |
| 403 | Capability/scope denied, application/subject mismatch |
| 404 | Resource not found |
| 409 | Conflict (already exists / revoked) |
| 413 | Payload/batch too large |
| 422 | Validation / context too large |
| 500 | Unhandled error (canonical envelope) |

## Cross-References

- Which feature each endpoint serves: [Feature Documentation](08_Feature_Documentation.md)
- Error envelope details: [Error Handling](17_Error_Handling.md)
- Authorization policies: [Security Design](16_Security_Design.md)
- Entities behind responses: [Data Model](14_Data_Model_Documentation.md)
