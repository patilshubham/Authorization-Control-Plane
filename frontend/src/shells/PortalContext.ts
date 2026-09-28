import { createContext, useContext } from "react";
import type { User } from "oidc-client-ts";

export type PortalContextValue = {
  user: User | null;
  onLogout: () => void;
};

export const PortalContext = createContext<PortalContextValue>({
  user: null,
  onLogout: () => {},
});

export function usePortal(): PortalContextValue {
  return useContext(PortalContext);
}
