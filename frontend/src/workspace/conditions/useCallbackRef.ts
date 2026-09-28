import { useEffect, useRef, useCallback } from "react";

/**
 * Returns a stable function identity that always calls the latest `callback`.
 * Lets effects depend on derived values without re-firing when the parent
 * passes a new inline callback on every render.
 */
export function useCallbackRef<Args extends unknown[], Return>(
  callback: (...args: Args) => Return,
): (...args: Args) => Return {
  const ref = useRef(callback);
  useEffect(() => {
    ref.current = callback;
  });
  return useCallback((...args: Args) => ref.current(...args), []);
}
