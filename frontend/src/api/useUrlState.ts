import { useCallback } from "react";
import { useSearchParams } from "react-router-dom";

/**
 * A `useState`-like hook whose string value is persisted in the URL query
 * string, so table search/filter selections survive reloads and are shareable
 * via a copied link. An empty value removes the parameter to keep URLs clean.
 *
 * Drop-in replacement for `useState<string>("")` on list/table filters:
 *   const [status, setStatus] = useUrlState("status");
 *
 * Updates use `replace` so filter changes do not spam browser history, and the
 * functional `setSearchParams` form is used so several independent filters on
 * one page never clobber each other's parameters.
 */
export function useUrlState(
  key: string,
  defaultValue = "",
): [string, (value: string) => void] {
  const [params, setParams] = useSearchParams();
  const value = params.get(key) ?? defaultValue;

  const setValue = useCallback(
    (next: string) => {
      setParams(
        (prev) => {
          const updated = new URLSearchParams(prev);
          if (!next) {
            updated.delete(key);
          } else {
            updated.set(key, next);
          }
          return updated;
        },
        { replace: true },
      );
    },
    [key, setParams],
  );

  return [value, setValue];
}
