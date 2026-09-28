import { useState } from "react";
import { SlideOver, Field, Segmented } from "../components/primitives";
import {
  useCreateApplication,
  useCreatePermission,
  useCreatePolicy,
  useCreateRole,
  usePermissions,
  useReferenceData,
  useTenants,
} from "../api/hooks";
import { ConditionBuilder } from "./conditions/ConditionBuilder";
import { ObligationsEditor } from "./ObligationsEditor";
import { serialize, emptyRoot } from "./conditions/model";
import { DEFAULT_RISK_LEVEL, POLICY_EFFECTS, RISK_LEVELS } from "../constants";

export type CreateKind = "role" | "permission" | "policy" | "application";

export function CreateForms({
  appId,
  kind,
  onClose,
}: {
  appId: string;
  kind: CreateKind | null;
  onClose: () => void;
}) {
  if (kind === "role") return <RoleForm appId={appId} onClose={onClose} />;
  if (kind === "permission")
    return <PermissionForm appId={appId} onClose={onClose} />;
  if (kind === "policy") return <PolicyForm appId={appId} onClose={onClose} />;
  if (kind === "application") return <ApplicationForm onClose={onClose} />;
  return null;
}

function RiskField({
  value,
  onChange,
}: {
  value: string;
  onChange: (v: string) => void;
}) {
  return (
    <Field label="Risk level">
      <Segmented
        ariaLabel="Risk level"
        value={value}
        onChange={onChange}
        options={RISK_LEVELS.map((r) => ({ value: r, label: r }))}
      />
    </Field>
  );
}

function RoleForm({ appId, onClose }: { appId: string; onClose: () => void }) {
  const create = useCreateRole(appId);
  const [roleKey, setRoleKey] = useState("");
  const [name, setName] = useState("");
  const [privileged, setPrivileged] = useState(false);
  const [riskLevel, setRiskLevel] = useState<string>(DEFAULT_RISK_LEVEL);
  const submit = () =>
    create.mutate(
      { roleKey, name, privileged, riskLevel },
      { onSuccess: onClose },
    );
  return (
    <SlideOver
      open
      title="New role"
      onClose={onClose}
      footer={
        <FormFooter
          onClose={onClose}
          onSubmit={submit}
          pending={create.isPending}
          disabled={!roleKey || !name}
        />
      }
    >
      <Field label="Role key" required hint="Stable identifier, e.g. invoice-approver">
        <input
          value={roleKey}
          onChange={(e) => setRoleKey(e.target.value)}
          aria-required="true"
        />
      </Field>
      <Field label="Display name" required>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          aria-required="true"
        />
      </Field>
      <RiskField value={riskLevel} onChange={setRiskLevel} />
      <label className="checkbox-row">
        <input
          type="checkbox"
          checked={privileged}
          onChange={(e) => setPrivileged(e.target.checked)}
        />{" "}
        Privileged role
      </label>
    </SlideOver>
  );
}

function PermissionForm({
  appId,
  onClose,
}: {
  appId: string;
  onClose: () => void;
}) {
  const create = useCreatePermission(appId);
  const [permissionKey, setPermissionKey] = useState("");
  const [resource, setResource] = useState("");
  const [action, setAction] = useState("");
  const [riskLevel, setRiskLevel] = useState<string>(DEFAULT_RISK_LEVEL);
  const submit = () =>
    create.mutate(
      { permissionKey, resource, action, riskLevel },
      { onSuccess: onClose },
    );
  return (
    <SlideOver
      open
      title="New permission"
      onClose={onClose}
      footer={
        <FormFooter
          onClose={onClose}
          onSubmit={submit}
          pending={create.isPending}
          disabled={!permissionKey || !resource || !action}
        />
      }
    >
      <Field label="Permission key" required hint="e.g. invoice.approve">
        <input
          value={permissionKey}
          onChange={(e) => setPermissionKey(e.target.value)}
          aria-required="true"
        />
      </Field>
      <Field label="Resource" required>
        <input
          value={resource}
          onChange={(e) => setResource(e.target.value)}
          aria-required="true"
        />
      </Field>
      <Field label="Action" required>
        <input
          value={action}
          onChange={(e) => setAction(e.target.value)}
          aria-required="true"
        />
      </Field>
      <RiskField value={riskLevel} onChange={setRiskLevel} />
    </SlideOver>
  );
}

function PolicyForm({
  appId,
  onClose,
}: {
  appId: string;
  onClose: () => void;
}) {
  const create = useCreatePolicy(appId);
  const permissions = usePermissions(appId);
  const referenceData = useReferenceData(appId);
  const [policyKey, setPolicyKey] = useState("");
  const [permissionKey, setPermissionKey] = useState("");
  const [effect, setEffect] = useState<(typeof POLICY_EFFECTS)[number]>(
    POLICY_EFFECTS[0],
  );
  const [conditions, setConditions] = useState(() => serialize(emptyRoot()));
  const [conditionsValid, setConditionsValid] = useState(true);
  const [priority, setPriority] = useState(0);
  const [obligations, setObligations] = useState("[]");
  const [publish, setPublish] = useState(false);
  const permissionKeys = (permissions.data ?? []).map((p) => p.permissionKey);
  const referenceDataKeys = (referenceData.data ?? []).map((r) => r.key);
  const submit = () =>
    create.mutate(
      { policyKey, permissionKey, effect, conditions, priority, obligations, publish },
      { onSuccess: onClose },
    );
  return (
    <SlideOver
      open
      title="New policy"
      onClose={onClose}
      wide
      footer={
        <FormFooter
          onClose={onClose}
          onSubmit={submit}
          pending={create.isPending}
          disabled={!policyKey || !permissionKey || !conditionsValid}
        />
      }
    >
      <Field label="Policy key" required>
        <input
          value={policyKey}
          onChange={(e) => setPolicyKey(e.target.value)}
          aria-required="true"
        />
      </Field>
      <Field label="Permission" required hint="Permission this policy governs">
        <input
          list="policy-permission-keys"
          value={permissionKey}
          placeholder="invoice.approve"
          onChange={(e) => setPermissionKey(e.target.value)}
          aria-required="true"
        />
        <datalist id="policy-permission-keys">
          {permissionKeys.map((k) => (
            <option key={k} value={k} />
          ))}
        </datalist>
      </Field>
      <Field
        label="Effect"
        hint={
          effect === "ALLOW"
            ? "Grants access when the conditions match."
            : "Blocks access when the conditions match (deny wins)."
        }
      >
        <Segmented
          ariaLabel="Effect"
          value={effect}
          onChange={setEffect}
          options={POLICY_EFFECTS.map((e) => ({ value: e, label: e }))}
        />
      </Field>
      <Field
        label="Conditions"
        hint="Build the rule visually — no JSON required."
      >
        <ConditionBuilder
          value={conditions}
          onChange={setConditions}
          onValidityChange={setConditionsValid}
          applicationId={appId}
          referenceDataKeys={referenceDataKeys}
          onAiEffectSuggested={setEffect}
        />
      </Field>
      <Field
        label="Priority"
        hint="Higher priority wins ties when several policies match."
      >
        <input
          type="number"
          value={priority}
          onChange={(e) => setPriority(Number(e.target.value) || 0)}
        />
      </Field>
      <Field
        label="Obligations"
        hint="Advisory instructions returned to the app when this policy decides."
      >
        <ObligationsEditor value={obligations} onChange={setObligations} />
      </Field>
      <label className="checkbox-row">
        <input
          type="checkbox"
          checked={publish}
          onChange={(e) => setPublish(e.target.checked)}
        />{" "}
        Publish immediately
      </label>
    </SlideOver>
  );
}

export function ApplicationForm({
  onClose,
  initialTenantId,
}: {
  onClose: () => void;
  initialTenantId?: string;
}) {
  const create = useCreateApplication();
  const tenants = useTenants();
  const [applicationId, setApplicationId] = useState("");
  const [name, setName] = useState("");
  const [tenantId, setTenantId] = useState(initialTenantId ?? "");
  const [riskLevel, setRiskLevel] = useState<string>(DEFAULT_RISK_LEVEL);
  const noTenants = (tenants.data ?? []).length === 0;
  const submit = () =>
    create.mutate(
      { applicationId, name, riskLevel, tenantId },
      { onSuccess: onClose },
    );
  return (
    <SlideOver
      open
      title="New application"
      onClose={onClose}
      footer={
        <FormFooter
          onClose={onClose}
          onSubmit={submit}
          pending={create.isPending}
          disabled={!applicationId || !name || !tenantId}
        />
      }
    >
      <Field label="Application ID" required>
        <input
          value={applicationId}
          onChange={(e) => setApplicationId(e.target.value)}
          aria-required="true"
        />
      </Field>
      <Field label="Name" required>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          aria-required="true"
        />
      </Field>
      <Field
        label="Owning tenant"
        required
        hint={
          noTenants
            ? "Create a tenant first — every application must belong to one."
            : "The organization that owns this application."
        }
      >
        <select
          value={tenantId}
          onChange={(e) => setTenantId(e.target.value)}
          disabled={noTenants}
          aria-required="true"
        >
          <option value="" disabled>
            Select a tenant…
          </option>
          {(tenants.data ?? []).map((t) => (
            <option key={t.tenantId} value={t.tenantId}>
              {t.name}
            </option>
          ))}
        </select>
      </Field>
      <RiskField value={riskLevel} onChange={setRiskLevel} />
    </SlideOver>
  );
}

function FormFooter({
  onClose,
  onSubmit,
  pending,
  disabled,
}: {
  onClose: () => void;
  onSubmit: () => void;
  pending: boolean;
  disabled: boolean;
}) {
  return (
    <>
      <button type="button" className="btn-secondary" onClick={onClose}>
        Cancel
      </button>
      <button
        type="button"
        className="btn-primary"
        onClick={onSubmit}
        disabled={pending || disabled}
      >
        {pending ? "Creating…" : "Create"}
      </button>
    </>
  );
}
