// AI feature availability. The server (`GET /v1/config`) is the single source of
// truth for whether AI is enabled and which features are on; the client only
// mirrors those flags to hide UI the API would otherwise reject.

import { useQuery } from "@tanstack/react-query";
import { portalApi } from "../apiClient";
import type { AiConfig, AiFeatureName, PortalConfig } from "../types";
import { qk } from "./queryKeys";
import { AI_DISABLED, getRuntimeConfig, setRuntimeConfig } from "./runtimeConfig";

export function usePortalConfig() {
  return useQuery<PortalConfig>({
    queryKey: qk.config,
    queryFn: async () => {
      const config = await portalApi.getConfig();
      // Publish to the runtime singleton so plain modules and state initializers
      // can read server-owned values synchronously.
      setRuntimeConfig(config);
      return config;
    },
    // Feature flags and runtime knobs rarely change within a session; avoid churn.
    staleTime: () => getRuntimeConfig().cache.configStaleMs,
  });
}

/** The AI configuration, defaulting to fully-disabled while loading or on error. */
export function useAiConfig(): AiConfig {
  const { data } = usePortalConfig();
  return data?.ai ?? AI_DISABLED;
}

/** Whether AI is enabled at all (master switch + valid provider on the server). */
export function useAiEnabled(): boolean {
  return useAiConfig().enabled;
}

/** Whether a specific AI feature is enabled (implies the master switch is on). */
export function useAiFeature(feature: AiFeatureName): boolean {
  const ai = useAiConfig();
  return ai.enabled && ai.features[feature];
}
