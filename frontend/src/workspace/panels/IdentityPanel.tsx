import { useEffect, useMemo, useState } from "react";
import {
  useCreateOidcProvider,
  useUpdateOidcProvider,
  useValidateOidcProvider,
  useOidcProviders,
} from "../../api/hooks";
import type { OidcProviderSummary } from "../../types";
import {
  EmptyBlock,
  Field,
  SlideOver,
  Spinner,
} from "../../components/primitives";
import { AppIcon } from "../../components/icons";
import { StatusChip } from "../../ui";
import { useToast } from "../../components/Toast";
import { OIDC_ALGORITHM_GROUPS, DEFAULT_OIDC_ALGORITHM } from "../../constants";
import { useCapabilities } from "../../capabilities";
import { formatDate } from "../formatters";

// ── Identity providers (OIDC) ───────────────────────────────────────────────────

/** Authentication flows a provider can represent, and how the authorization subject is derived. */
const SUBJECT_TYPES = [
  {
    value: "SERVICE_ACCOUNT",
    label: "Machine-to-machine",
    hint: "Client-credentials tokens. The subject is the calling service.",
    defaultClaim: "azp",
    claimLabel: "Client ID claim",
    claimHint:
      "Claim that carries the calling client/service identifier (e.g. azp or client_id).",
  },
  {
    value: "USER",
    label: "User",
    hint: "Interactive / authorization-code tokens. The subject is the signed-in user.",
    defaultClaim: "sub",
    claimLabel: "User email claim",
    claimHint:
      "Claim that carries the user's identity (e.g. email, preferred_username or sub).",
  },
] as const;

type SubjectTypeValue = (typeof SUBJECT_TYPES)[number]["value"];

function subjectTypeMeta(value: string) {
  return SUBJECT_TYPES.find((t) => t.value === value) ?? SUBJECT_TYPES[1];
}

/** Federation providers should use asymmetric signatures; flag anything else as worth reviewing. */
function algorithmsAreStrong(algorithms: string[]): boolean {
  return (
    algorithms.length > 0 &&
    algorithms.every(
      (a) =>
        /^(RS|ES|PS)\d{3}$/i.test(a.trim()) ||
        a.trim().toUpperCase() === "EDDSA",
    )
  );
}

type ProviderFormState = {
  issuer: string;
  audience: string;
  jwksUri: string;
  algorithms: string[];
  subjectType: SubjectTypeValue;
  subjectClaim: string;
};

const EMPTY_PROVIDER_FORM: ProviderFormState = {
  issuer: "",
  audience: "",
  jwksUri: "",
  algorithms: [DEFAULT_OIDC_ALGORITHM],
  subjectType: "USER",
  subjectClaim: "sub",
};

export function IdentityPanel({ appId }: { appId: string }) {
  const providers = useOidcProviders(appId);
  const create = useCreateOidcProvider(appId);
  const update = useUpdateOidcProvider(appId);
  const toast = useToast();
  const canManageApplication = useCapabilities().can(
    "ManageApplication",
    appId,
  );

  const [query, setQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState<
    "all" | "enabled" | "disabled"
  >("all");
  const [createOpen, setCreateOpen] = useState(false);
  const [createForm, setCreateForm] =
    useState<ProviderFormState>(EMPTY_PROVIDER_FORM);
  const [editing, setEditing] = useState<OidcProviderSummary | null>(null);
  const [editForm, setEditForm] =
    useState<ProviderFormState>(EMPTY_PROVIDER_FORM);
  // A failing "Validate configuration" result blocks the corresponding save until resolved.
  const [createHasFail, setCreateHasFail] = useState(false);
  const [editHasFail, setEditHasFail] = useState(false);

  const all = providers.data ?? [];
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return all.filter((p) => {
      if (statusFilter === "enabled" && !p.enabled) return false;
      if (statusFilter === "disabled" && p.enabled) return false;
      if (!q) return true;
      return (
        p.issuer.toLowerCase().includes(q) ||
        p.audience.toLowerCase().includes(q)
      );
    });
  }, [all, query, statusFilter]);

  const enabledCount = all.filter((p) => p.enabled).length;

  const submitCreate = () =>
    create.mutate(
      {
        issuer: createForm.issuer.trim(),
        audience: createForm.audience.trim(),
        jwksUri: createForm.jwksUri.trim(),
        allowedAlgorithms: createForm.algorithms,
        subjectType: createForm.subjectType,
        subjectClaim: createForm.subjectClaim.trim(),
      },
      {
        onSuccess: () => {
          setCreateOpen(false);
          setCreateForm(EMPTY_PROVIDER_FORM);
        },
      },
    );

  const openEdit = (p: OidcProviderSummary) => {
    setEditing(p);
    setEditHasFail(false);
    setEditForm({
      issuer: p.issuer,
      audience: p.audience,
      jwksUri: p.jwksUri,
      algorithms: [...p.allowedAlgorithms],
      subjectType: subjectTypeMeta(p.subjectType).value,
      subjectClaim: p.subjectClaim,
    });
  };

  const submitEdit = () => {
    if (!editing) return;
    update.mutate(
      {
        id: editing.id,
        issuer: editForm.issuer.trim(),
        audience: editForm.audience.trim(),
        jwksUri: editForm.jwksUri.trim(),
        allowedAlgorithms: editForm.algorithms,
        subjectType: editForm.subjectType,
        subjectClaim: editForm.subjectClaim.trim(),
        enabled: editing.enabled,
      },
      { onSuccess: () => setEditing(null) },
    );
  };

  const toggleEnabled = (p: OidcProviderSummary) =>
    update.mutate({
      id: p.id,
      issuer: p.issuer,
      audience: p.audience,
      jwksUri: p.jwksUri,
      allowedAlgorithms: p.allowedAlgorithms,
      subjectType: subjectTypeMeta(p.subjectType).value,
      subjectClaim: p.subjectClaim,
      enabled: !p.enabled,
    });

  const copy = (label: string, value: string) => {
    void navigator.clipboard
      ?.writeText(value)
      .then(() => toast.success(`${label} copied.`))
      .catch(() => toast.error("Copy failed."));
  };

  const createValid =
    !!createForm.issuer.trim() &&
    !!createForm.audience.trim() &&
    !!createForm.jwksUri.trim() &&
    createForm.algorithms.length > 0 &&
    !!createForm.subjectClaim.trim() &&
    !create.isPending;
  const editValid =
    !!editForm.issuer.trim() &&
    !!editForm.audience.trim() &&
    !!editForm.jwksUri.trim() &&
    editForm.algorithms.length > 0 &&
    !!editForm.subjectClaim.trim() &&
    !update.isPending;

  return (
    <article className="inspector">
      <header className="inspector-head">
        <div className="inspector-title-row">
          <h1>Identity providers</h1>
          <span className="muted">
            {all.length ? `${enabledCount} of ${all.length} enabled` : "0"}
          </span>
        </div>
        <div className="inspector-action-bar">
          {canManageApplication && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => {
                setCreateForm(EMPTY_PROVIDER_FORM);
                setCreateHasFail(false);
                setCreateOpen(true);
              }}
            >
              Register provider
            </button>
          )}
        </div>
      </header>

      {providers.isLoading ? (
        <Spinner label="Loading providers…" />
      ) : all.length === 0 ? (
        <EmptyBlock
          title="No identity providers yet"
          hint="Federate authentication by registering an OIDC issuer. You'll need the issuer URL, the audience (client/API identifier) and the JWKS endpoint that publishes its signing keys."
          action={
            canManageApplication ? (
              <button
                type="button"
                className="btn-primary"
                onClick={() => {
                  setCreateForm(EMPTY_PROVIDER_FORM);
                  setCreateHasFail(false);
                  setCreateOpen(true);
                }}
              >
                Register your first provider
              </button>
            ) : undefined
          }
        />
      ) : (
        <>
          <div className="idp-controls">
            <input
              className="idp-search"
              placeholder="Search issuer or audience…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              aria-label="Search providers"
            />
            <div
              className="idp-filter"
              role="group"
              aria-label="Filter by status"
            >
              {(["all", "enabled", "disabled"] as const).map((s) => (
                <button
                  key={s}
                  type="button"
                  className={statusFilter === s ? "is-active" : ""}
                  onClick={() => setStatusFilter(s)}
                >
                  {s === "all"
                    ? "All"
                    : s === "enabled"
                      ? "Enabled"
                      : "Disabled"}
                </button>
              ))}
            </div>
          </div>

          {filtered.length === 0 ? (
            <EmptyBlock
              title="No matching providers"
              hint="Try a different search term or filter."
            />
          ) : (
            <div className="idp-grid">
              {filtered.map((p) => {
                const strong = algorithmsAreStrong(p.allowedAlgorithms);
                return (
                  <section
                    key={p.id}
                    className={`idp-card ${p.enabled ? "" : "is-disabled"}`}
                  >
                    <header className="idp-card-head">
                      <span className="idp-type" title="OpenID Connect">
                        {p.providerType}
                      </span>
                      <StatusChip value={p.enabled ? "ENABLED" : "DISABLED"} />
                    </header>
                    <h2 className="idp-issuer" title={p.issuer}>
                      {p.issuer}
                    </h2>

                    <dl className="idp-meta">
                      <div>
                        <dt>Audience</dt>
                        <dd>
                          <code>{p.audience}</code>
                          <button
                            type="button"
                            className="copy-btn"
                            title="Copy audience"
                            onClick={() => copy("Audience", p.audience)}
                          >
                            <AppIcon name="copy" size={14} />
                          </button>
                        </dd>
                      </div>
                      <div>
                        <dt>JWKS URI</dt>
                        <dd>
                          <code className="truncate" title={p.jwksUri}>
                            {p.jwksUri}
                          </code>
                          <button
                            type="button"
                            className="copy-btn"
                            title="Copy JWKS URI"
                            onClick={() => copy("JWKS URI", p.jwksUri)}
                          >
                            <AppIcon name="copy" size={14} />
                          </button>
                        </dd>
                      </div>
                      <div>
                        <dt>Subject</dt>
                        <dd>
                          <span title={subjectTypeMeta(p.subjectType).hint}>
                            {subjectTypeMeta(p.subjectType).label}
                          </span>{" "}
                          &middot;{" "}
                          <code
                            title={subjectTypeMeta(p.subjectType).claimLabel}
                          >
                            {p.subjectClaim}
                          </code>
                        </dd>
                      </div>
                    </dl>

                    <div className="idp-algos">
                      <span
                        className={`idp-shield ${strong ? "ok" : "warn"}`}
                        title={
                          strong
                            ? "Asymmetric signing keys"
                            : "Review signing algorithms"
                        }
                      >
                        {strong ? "✓ Secure signing" : "⚠ Review signing"}
                      </span>
                      {p.allowedAlgorithms.map((a) => (
                        <span key={a} className="algo-chip">
                          {a}
                        </span>
                      ))}
                    </div>

                    <footer className="idp-card-foot">
                      <span
                        className="muted"
                        title={p.createdBy ? `by ${p.createdBy}` : undefined}
                      >
                        {p.updatedAt
                          ? `Updated ${formatDate(p.updatedAt)}`
                          : `Added ${formatDate(p.createdAt)}`}
                      </span>
                      <div className="idp-card-actions">
                        {canManageApplication && (
                          <button
                            type="button"
                            className="mini-btn"
                            onClick={() => openEdit(p)}
                          >
                            Edit
                          </button>
                        )}
                        {canManageApplication && (
                          <button
                            type="button"
                            className="mini-btn"
                            disabled={update.isPending}
                            onClick={() => toggleEnabled(p)}
                          >
                            {p.enabled ? "Disable" : "Enable"}
                          </button>
                        )}
                      </div>
                    </footer>
                  </section>
                );
              })}
            </div>
          )}
        </>
      )}

      <SlideOver
        open={createOpen}
        title="Register OIDC provider"
        onClose={() => setCreateOpen(false)}
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setCreateOpen(false)}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              disabled={!createValid || createHasFail}
              onClick={submitCreate}
            >
              {create.isPending ? "Registering…" : "Register"}
            </button>
          </>
        }
      >
        <ProviderFormFields
          appId={appId}
          form={createForm}
          onChange={setCreateForm}
          onValidationBlockChange={setCreateHasFail}
        />
      </SlideOver>

      <SlideOver
        open={!!editing}
        title="Edit identity provider"
        onClose={() => setEditing(null)}
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setEditing(null)}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              disabled={!editValid || editHasFail}
              onClick={submitEdit}
            >
              {update.isPending ? "Saving…" : "Save changes"}
            </button>
          </>
        }
      >
        <ProviderFormFields
          appId={appId}
          form={editForm}
          onChange={setEditForm}
          onValidationBlockChange={setEditHasFail}
        />
      </SlideOver>
    </article>
  );
}

function ProviderFormFields({
  appId,
  form,
  onChange,
  onValidationBlockChange,
}: {
  appId: string;
  form: ProviderFormState;
  onChange: (f: ProviderFormState) => void;
  onValidationBlockChange?: (hasFail: boolean) => void;
}) {
  const meta = subjectTypeMeta(form.subjectType);
  const validate = useValidateOidcProvider(appId);
  const { reset: resetValidation } = validate;

  // A validation result only reflects the inputs it was run against. Once any field changes the
  // result is stale, so clear it — this un-blocks Save and hides the outdated checks until the
  // admin re-validates.
  useEffect(() => {
    resetValidation();
  }, [
    resetValidation,
    form.issuer,
    form.audience,
    form.jwksUri,
    form.subjectClaim,
    form.subjectType,
    form.algorithms,
  ]);

  const failingChecks = validate.data
    ? validate.data.checks.filter((c) => c.status === "FAIL")
    : [];
  const hasFail = failingChecks.length > 0;

  // Report the blocking state up so the parent can disable Save while any check fails.
  useEffect(() => {
    onValidationBlockChange?.(hasFail);
  }, [hasFail, onValidationBlockChange]);

  const selectType = (value: SubjectTypeValue) => {
    if (value === form.subjectType) return;
    onChange({
      ...form,
      subjectType: value,
      subjectClaim: subjectTypeMeta(value).defaultClaim,
    });
  };
  return (
    <>
      <Field label="Issuer" required hint="The OIDC issuer URL (iss claim).">
        <input
          value={form.issuer}
          onChange={(e) => onChange({ ...form, issuer: e.target.value })}
          placeholder="https://issuer.example.com/realms/app"
          aria-required="true"
        />
      </Field>
      <Field
        label="Audience"
        required
        hint="The client or API identifier tokens are issued for (aud claim)."
      >
        <input
          value={form.audience}
          onChange={(e) => onChange({ ...form, audience: e.target.value })}
          placeholder="authorization-api"
          aria-required="true"
        />
      </Field>
      <Field
        label="JWKS URI"
        required
        hint="Endpoint publishing the provider's public signing keys."
      >
        <input
          value={form.jwksUri}
          onChange={(e) => onChange({ ...form, jwksUri: e.target.value })}
          placeholder="https://issuer.example.com/.well-known/jwks.json"
          aria-required="true"
        />
      </Field>
      <Field label="Authentication type" hint={meta.hint}>
        <div
          className="idp-filter"
          role="group"
          aria-label="Authentication type"
        >
          {SUBJECT_TYPES.map((t) => (
            <button
              key={t.value}
              type="button"
              className={form.subjectType === t.value ? "is-active" : ""}
              aria-pressed={form.subjectType === t.value}
              onClick={() => selectType(t.value)}
            >
              {t.label}
            </button>
          ))}
        </div>
      </Field>
      <Field label={meta.claimLabel} required hint={meta.claimHint}>
        <input
          value={form.subjectClaim}
          onChange={(e) => onChange({ ...form, subjectClaim: e.target.value })}
          placeholder={meta.defaultClaim}
          aria-required="true"
        />
      </Field>
      <Field
        label="Allowed algorithms"
        required
        hint="Signature algorithms accepted from this provider. Symmetric (HS*) and 'none' are not permitted."
      >
        <AlgorithmPicker
          selected={form.algorithms}
          onChange={(algorithms) => onChange({ ...form, algorithms })}
        />
      </Field>
      <div className="idp-validate">
        <button
          type="button"
          className="btn-secondary"
          disabled={validate.isPending}
          onClick={() =>
            validate.mutate({
              issuer: form.issuer.trim(),
              audience: form.audience.trim(),
              jwksUri: form.jwksUri.trim(),
              allowedAlgorithms: form.algorithms,
              subjectClaim: form.subjectClaim.trim(),
            })
          }
        >
          {validate.isPending ? "Validating…" : "Validate configuration"}
        </button>
        {validate.data && (
          <ul className="idp-checks" aria-live="polite">
            {validate.data.checks.map((c) => (
              <li
                key={c.label}
                className={`idp-check idp-check-${c.status.toLowerCase()}`}
              >
                <span className="idp-check-badge">{c.status}</span>
                <span className="idp-check-body">
                  <strong>{c.label}</strong>
                  <span className="muted">{c.detail}</span>
                </span>
              </li>
            ))}
          </ul>
        )}
        {hasFail && (
          <p className="field-error" role="alert">
            Saving is blocked until{" "}
            {failingChecks.length === 1
              ? "the failed check is"
              : `all ${failingChecks.length} failed checks are`}{" "}
            resolved. Fix the highlighted fields and validate again.
          </p>
        )}
        {validate.isError && (
          <p className="field-warning">Could not validate configuration.</p>
        )}
      </div>
    </>
  );
}

function AlgorithmPicker({
  selected,
  onChange,
}: {
  selected: string[];
  onChange: (algorithms: string[]) => void;
}) {
  const toggle = (algorithm: string) =>
    onChange(
      selected.includes(algorithm)
        ? selected.filter((a) => a !== algorithm)
        : [...selected, algorithm],
    );
  return (
    <div className="algo-picker" role="group" aria-label="Allowed algorithms">
      {OIDC_ALGORITHM_GROUPS.map((group) => (
        <fieldset key={group.family} className="algo-group">
          <legend title={group.description}>{group.family}</legend>
          <div className="algo-options">
            {group.algorithms.map((algorithm) => {
              const on = selected.includes(algorithm);
              return (
                <button
                  key={algorithm}
                  type="button"
                  className={`algo-toggle ${on ? "is-on" : ""}`}
                  aria-pressed={on}
                  onClick={() => toggle(algorithm)}
                >
                  {on ? "✓ " : ""}
                  {algorithm}
                </button>
              );
            })}
          </div>
        </fieldset>
      ))}
      {selected.length === 0 && (
        <p className="field-warning">Select at least one algorithm.</p>
      )}
    </div>
  );
}
