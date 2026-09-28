import { describe, expect, it } from "vitest";
import type {
  PermissionSummary,
  PolicySummary,
  RoleSummary,
} from "../../types";
import {
  buildAppAccessTree,
  buildDecisionPath,
  buildPermissionReverseTree,
  buildRoleSubtree,
  buildTenantAppTree,
  buildTenantSubtree,
  type TenantAppRow,
} from "./graphModel";

const roles: RoleSummary[] = [
  {
    roleKey: "editor",
    name: "Editor",
    privileged: false,
    riskLevel: "MEDIUM",
    status: "ACTIVE",
    permissions: ["doc:write", "doc:read"],
  },
  {
    roleKey: "viewer",
    name: "Viewer",
    privileged: false,
    riskLevel: "LOW",
    status: "ACTIVE",
    permissions: ["doc:read"],
  },
];

const permissions: PermissionSummary[] = [
  {
    id: "1",
    permissionKey: "doc:read",
    resource: "doc",
    action: "read",
    riskLevel: "LOW",
    status: "ACTIVE",
  },
  {
    id: "2",
    permissionKey: "doc:write",
    resource: "doc",
    action: "write",
    riskLevel: "HIGH",
    status: "ACTIVE",
  },
];

const policies: PolicySummary[] = [
  {
    policyKey: "biz-hours",
    permissionKey: "doc:write",
    effect: "ALLOW",
    state: "PUBLISHED",
  },
];

describe("buildAppAccessTree", () => {
  it("nests application → roles → permissions → policies", () => {
    const tree = buildAppAccessTree(
      "app1",
      "Docs",
      roles,
      permissions,
      policies,
    );
    expect(tree.id).toBe("application:app1");
    expect(tree.children).toHaveLength(2);

    const editor = tree.children?.find((c) => c.id === "role:editor");
    expect(editor?.children).toHaveLength(2);
    const write = editor?.children?.find(
      (c) => c.id === "permission:doc:write",
    );
    expect(write?.children).toHaveLength(1);
    expect(write?.children?.[0].id).toBe("policy:biz-hours");
  });

  it("omits permission children when a role grants none", () => {
    const viewer = buildRoleSubtree(roles[1], permissions, policies);
    expect(viewer.id).toBe("role:viewer");
    expect(viewer.children).toHaveLength(1);
    expect(viewer.children?.[0].children).toBeUndefined();
  });
});

describe("buildPermissionReverseTree", () => {
  it("attaches granting roles and related policies as children", () => {
    const perm = permissions[1]; // doc:write
    const granting = roles.filter((r) =>
      r.permissions.includes(perm.permissionKey),
    );
    const related = policies.filter(
      (p) => p.permissionKey === perm.permissionKey,
    );
    const tree = buildPermissionReverseTree(perm, granting, related);
    expect(tree.id).toBe("permission:doc:write");
    expect(tree.children).toHaveLength(2);
    expect(tree.children?.map((c) => c.kind).sort()).toEqual([
      "policy",
      "role",
    ]);
  });
});

describe("buildTenantAppTree / buildTenantSubtree", () => {
  const rows: TenantAppRow[] = [
    {
      tenantId: "t1",
      name: "Acme",
      status: "ACTIVE",
      apps: [
        { applicationId: "a1", name: "Finance", riskLevel: "MEDIUM" },
        { applicationId: "a2", name: "HR", riskLevel: "LOW" },
      ],
    },
  ];

  it("builds root → tenant → application", () => {
    const tree = buildTenantAppTree("All tenants", rows);
    expect(tree.kind).toBe("root");
    expect(tree.children).toHaveLength(1);
    expect(tree.children?.[0].children).toHaveLength(2);
    expect(tree.children?.[0].children?.[0].id).toBe("application:a1");
  });

  it("builds a single tenant subtree", () => {
    const tree = buildTenantSubtree("t1", "Acme", rows[0].apps);
    expect(tree.id).toBe("tenant:t1");
    expect(tree.children).toHaveLength(2);
  });
});

describe("buildDecisionPath", () => {
  it("chains subject → roles → permissions → policies → verdict", () => {
    const tree = buildDecisionPath("user@x.io", true, {
      matchedRoles: ["editor"],
      matchedPermissions: ["doc:write"],
      matchedPolicies: ["biz-hours"],
    });
    const roleLayer = tree.children?.[0];
    const permLayer = roleLayer?.children?.[0];
    const policyLayer = permLayer?.children?.[0];
    const verdict = policyLayer?.children?.[0];
    expect(roleLayer?.label).toBe("Roles");
    expect(permLayer?.label).toBe("Permissions");
    expect(policyLayer?.label).toBe("Policies");
    expect(verdict?.label).toBe("ALLOWED");
    expect(verdict?.riskLevel).toBe("LOW");
  });

  it("surfaces the deny reason on a denied verdict", () => {
    const tree = buildDecisionPath("user@x.io", false, undefined, "no role");
    const verdict =
      tree.children?.[0]?.children?.[0]?.children?.[0]?.children?.[0];
    expect(verdict?.label).toBe("DENIED");
    expect(verdict?.sublabel).toBe("no role");
    expect(verdict?.riskLevel).toBe("CRITICAL");
  });
});
