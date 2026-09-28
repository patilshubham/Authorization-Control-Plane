import { useOutletContext } from "react-router-dom";
import type { ApplicationSummary } from "../types";
import type { CreateKind } from "../workspace/CreateForms";

export type AppOutletContext = {
  appId: string;
  app: ApplicationSummary;
  tenantName: string;
  startCreate: (kind: CreateKind) => void;
};

export function useAppContext(): AppOutletContext {
  return useOutletContext<AppOutletContext>();
}
