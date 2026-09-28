import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type ButtonHTMLAttributes,
  type ReactNode,
} from "react";
import { AppIcon } from "./icons";

// ── Button ─────────────────────────────────────────────────────────────────────

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";
type ButtonSize = "sm" | "md";

export function Button({
  variant = "secondary",
  size = "md",
  loading = false,
  icon,
  iconEnd,
  children,
  className,
  disabled,
  type = "button",
  ...rest
}: {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
  icon?: ReactNode;
  iconEnd?: ReactNode;
} & ButtonHTMLAttributes<HTMLButtonElement>) {
  const classes = [
    "btn",
    `btn-${variant}`,
    size === "sm" ? "btn-sm" : "",
    loading ? "btn-loading" : "",
    className ?? "",
  ]
    .filter(Boolean)
    .join(" ");
  return (
    <button
      type={type}
      className={classes}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      {...rest}
    >
      {loading && <span className="btn-spinner" aria-hidden="true" />}
      {!loading && icon && (
        <span className="btn-icon" aria-hidden="true">
          {icon}
        </span>
      )}
      {children != null && <span className="btn-label">{children}</span>}
      {!loading && iconEnd && (
        <span className="btn-icon" aria-hidden="true">
          {iconEnd}
        </span>
      )}
    </button>
  );
}

// ── Keyboard hint ─────────────────────────────────────────────────────────────

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="kbd">{children}</kbd>;
}

// ── Risk indicator ────────────────────────────────────────────────────────────

export function RiskDot({ level }: { level: string }) {
  return (
    <span
      className={`risk-dot risk-${level.toLowerCase()}`}
      title={`${level} risk`}
      aria-hidden="true"
    />
  );
}

// ── State badge (DRAFT / PUBLISHED / ACTIVE / REVOKED …) ────────────────────────

export function StateBadge({ value }: { value: string }) {
  return (
    <span className={`state-badge state-${value.toLowerCase()}`}>{value}</span>
  );
}

// ── Chip ───────────────────────────────────────────────────────────────────────

export function Chip({
  children,
  tone = "neutral",
  onRemove,
  title,
}: {
  children: ReactNode;
  tone?: "neutral" | "granted" | "draft" | "danger";
  onRemove?: () => void;
  title?: string;
}) {
  return (
    <span className={`chip chip-${tone}`} title={title}>
      {children}
      {onRemove && (
        <button
          type="button"
          className="chip-remove"
          aria-label="Remove"
          onClick={onRemove}
        >
          ×
        </button>
      )}
    </span>
  );
}

// ── Toggle switch ────────────────────────────────────────────────────────────

export function Toggle({
  checked,
  onChange,
  label,
  disabled,
}: {
  checked: boolean;
  onChange: (next: boolean) => void;
  label: string;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      className={`toggle${checked ? " toggle-on" : ""}`}
      onClick={() => onChange(!checked)}
    >
      <span className="toggle-knob" />
    </button>
  );
}

// ── Inline-editable text ───────────────────────────────────────────────────────

export function InlineText({
  value,
  placeholder = "—",
  onCommit,
  ariaLabel,
  multiline,
}: {
  value: string;
  placeholder?: string;
  onCommit: (next: string) => void;
  ariaLabel: string;
  multiline?: boolean;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(value);
  const ref = useRef<HTMLInputElement & HTMLTextAreaElement>(null);

  useEffect(() => setDraft(value), [value]);
  useLayoutEffect(() => {
    if (editing) ref.current?.focus();
  }, [editing]);

  const commit = () => {
    setEditing(false);
    const trimmed = draft.trim();
    if (trimmed !== value) onCommit(trimmed);
  };

  if (!editing) {
    return (
      <button
        type="button"
        className="inline-text"
        onClick={() => setEditing(true)}
        aria-label={`Edit ${ariaLabel}`}
      >
        {value ? (
          value
        ) : (
          <span className="inline-text-empty">{placeholder}</span>
        )}
        <span className="inline-text-pencil" aria-hidden="true">
          ✎
        </span>
      </button>
    );
  }

  const commonProps = {
    ref,
    value: draft,
    "aria-label": ariaLabel,
    className: "inline-input",
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
      setDraft(e.target.value),
    onBlur: commit,
    onKeyDown: (e: React.KeyboardEvent) => {
      if (e.key === "Enter" && !multiline) commit();
      if (e.key === "Escape") {
        setDraft(value);
        setEditing(false);
      }
    },
  };

  return multiline ? (
    <textarea rows={3} {...commonProps} />
  ) : (
    <input type="text" {...commonProps} />
  );
}

// ── Segmented control ──────────────────────────────────────────────────────────

export function Segmented<T extends string>({
  options,
  value,
  onChange,
  ariaLabel,
}: {
  options: Array<{ value: T; label: string }>;
  value: T;
  onChange: (v: T) => void;
  ariaLabel: string;
}) {
  return (
    <div className="segmented" role="radiogroup" aria-label={ariaLabel}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          className={`segmented-item${value === o.value ? " active" : ""}`}
          onClick={() => onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

// ── Field wrapper ────────────────────────────────────────────────────────────

export function Field({
  label,
  hint,
  required,
  error,
  children,
}: {
  label: string;
  hint?: string;
  required?: boolean;
  error?: string;
  children: ReactNode;
}) {
  return (
    <label className={`field${error ? " field-invalid" : ""}`}>
      <span className="field-label">
        {label}
        {required && (
          <>
            <span className="field-required" aria-hidden="true">
              {" "}
              *
            </span>
            <span className="visually-hidden"> (required)</span>
          </>
        )}
      </span>
      {children}
      {error ? (
        <span className="field-error" role="alert">
          {error}
        </span>
      ) : (
        hint && <span className="field-hint">{hint}</span>
      )}
    </label>
  );
}

// ── Modal accessibility hook ────────────────────────────────────────────────────

/**
 * Shared accessible-modal behavior for centered dialog surfaces. While `open`,
 * it locks background scroll, closes on Escape, moves focus into the dialog,
 * traps Tab focus within it, and restores focus to the previously-focused
 * element on close. Returns a ref to attach to the dialog container (give the
 * container `tabIndex={-1}` so it can receive focus when it has no focusables).
 */
export function useModalDialog<T extends HTMLElement>(
  open: boolean,
  onClose: () => void,
) {
  const ref = useRef<T>(null);
  // Hold the latest onClose in a ref so the effect depends only on `open` and
  // does not tear down / re-run (which would disrupt focus) on parent renders.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    if (!open) return;
    const previouslyFocused = document.activeElement as HTMLElement | null;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    const focusables = () => {
      const container = ref.current;
      if (!container) return [] as HTMLElement[];
      return Array.from(
        container.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
        ),
      ).filter((el) => el.offsetParent !== null);
    };

    (focusables()[0] ?? ref.current)?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        onCloseRef.current();
        return;
      }
      if (e.key !== "Tab") return;
      const items = focusables();
      if (items.length === 0) {
        e.preventDefault();
        ref.current?.focus();
        return;
      }
      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;
      if (e.shiftKey && active === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && active === last) {
        e.preventDefault();
        first.focus();
      }
    };

    window.addEventListener("keydown", onKey);
    return () => {
      document.body.style.overflow = previousOverflow;
      window.removeEventListener("keydown", onKey);
      previouslyFocused?.focus?.();
    };
  }, [open]);

  return ref;
}

// ── Slide-over panel (right-anchored, for focused create flows) ─────────────────


export function SlideOver({
  open,
  title,
  onClose,
  children,
  footer,
  wide,
  dismissOnBackdrop = true,
}: {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
  dismissOnBackdrop?: boolean;
}) {
  useEffect(() => {
    if (!open) return;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => {
      document.body.style.overflow = previousOverflow;
      window.removeEventListener("keydown", onKey);
    };
  }, [open, onClose]);

  if (!open) return null;
  return (
    <div
      className="slideover-backdrop"
      role="presentation"
      onClick={dismissOnBackdrop ? onClose : undefined}
    >
      <section
        className={`slideover${wide ? " slideover-wide" : ""}`}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onClick={(e) => e.stopPropagation()}
      >
        <header className="slideover-header">
          <h2>{title}</h2>
          <button
            type="button"
            className="icon-btn"
            aria-label="Close"
            onClick={onClose}
          >
            ×
          </button>
        </header>
        <div className="slideover-body">{children}</div>
        {footer && <footer className="slideover-footer">{footer}</footer>}
      </section>
    </div>
  );
}

// ── Empty block ────────────────────────────────────────────────────────────────

export function EmptyBlock({
  icon = <AppIcon name="empty" size={40} />,
  title,
  hint,
  action,
}: {
  icon?: ReactNode;
  title: string;
  hint?: string;
  action?: ReactNode;
}) {
  return (
    <div className="empty-block">
      <span className="empty-icon" aria-hidden="true">
        {icon}
      </span>
      <p className="empty-title">{title}</p>
      {hint && <p className="empty-hint">{hint}</p>}
      {action}
    </div>
  );
}

// ── Spinner ────────────────────────────────────────────────────────────────────

export function Spinner({ label }: { label?: string }) {
  return (
    <div className="spinner-row" aria-busy="true">
      <span className="spinner" aria-hidden="true" />
      {label && <span>{label}</span>}
    </div>
  );
}

// ── Contextual help (info icon with an accessible popover) ──────────────────────

export function InfoHint({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDocClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node))
        setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDocClick);
    window.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDocClick);
      window.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <span className="info-hint" ref={ref}>
      <button
        type="button"
        className="info-hint-trigger"
        aria-label={`Help: ${label}`}
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        ⓘ
      </button>
      {open && (
        <span className="info-hint-popover" role="tooltip">
          {children}
        </span>
      )}
    </span>
  );
}
