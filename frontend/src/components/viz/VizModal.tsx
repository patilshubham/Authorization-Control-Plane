// VizModal — a large dialog frame for immersive graph views.
//
// Reuses the app's dialog visual language (backdrop + surface panel) but sizes
// up for visualizations, and adds a legend row and standard a11y affordances:
// labelled dialog, Escape / backdrop to close, focus moved in on open and a
// simple Tab focus trap so keyboard users stay within the modal.

import { useEffect, useRef, type ReactNode } from "react";

export function VizModal({
  title,
  subtitle,
  open,
  onClose,
  legend,
  children,
}: {
  title: string;
  subtitle?: string;
  open: boolean;
  onClose: () => void;
  legend?: ReactNode;
  children: ReactNode;
}) {
  const panelRef = useRef<HTMLDivElement | null>(null);
  const closeRef = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const previouslyFocused = document.activeElement as HTMLElement | null;
    closeRef.current?.focus();

    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
        return;
      }
      if (e.key !== "Tab" || !panelRef.current) return;
      const focusable = panelRef.current.querySelectorAll<HTMLElement>(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])',
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };

    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      previouslyFocused?.focus?.();
    };
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div
      className="dialog-backdrop viz-backdrop"
      role="presentation"
      onClick={onClose}
    >
      <div
        ref={panelRef}
        className="viz-modal"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onClick={(e) => e.stopPropagation()}
      >
        <header className="viz-modal-head">
          <div className="viz-modal-titles">
            <h2 className="viz-modal-title">{title}</h2>
            {subtitle && <p className="viz-modal-sub">{subtitle}</p>}
          </div>
          <button
            ref={closeRef}
            type="button"
            className="viz-modal-close"
            aria-label="Close"
            onClick={onClose}
          >
            ×
          </button>
        </header>
        {legend && <div className="viz-legend">{legend}</div>}
        <div className="viz-modal-body">{children}</div>
      </div>
    </div>
  );
}
