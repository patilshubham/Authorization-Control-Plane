/** @vitest-environment jsdom */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { useAiEnabled, useAiFeature } from "./aiConfig";
import { DEFAULT_PORTAL_CONFIG } from "./runtimeConfig";
import type { PortalConfig } from "../types";

function Probe() {
  const enabled = useAiEnabled();
  const policyAuthoring = useAiFeature("policyAuthoring");
  const accessSearch = useAiFeature("accessSearch");
  return (
    <span data-testid="out">{`${enabled}|${policyAuthoring}|${accessSearch}`}</span>
  );
}

function renderWithConfig(config: Partial<PortalConfig> | undefined): {
  text: string;
  cleanup: () => void;
} {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  if (config) {
    // Pre-seed the cache so the hooks resolve synchronously without fetching.
    // Merge over the defaults so AI-only fixtures still form a full PortalConfig.
    client.setQueryData(["config"], { ...DEFAULT_PORTAL_CONFIG, ...config });
  }

  const container = document.createElement("div");
  document.body.appendChild(container);
  let root: Root;
  act(() => {
    root = createRoot(container);
    root.render(
      <QueryClientProvider client={client}>
        <Probe />
      </QueryClientProvider>,
    );
  });

  return {
    text: container.textContent ?? "",
    cleanup: () => {
      act(() => root.unmount());
      container.remove();
    },
  };
}

describe("useAiFeature / useAiEnabled", () => {
  let cleanup: () => void = () => {};

  beforeEach(() => {
    cleanup = () => {};
  });

  afterEach(() => {
    cleanup();
  });

  it("reports disabled while no config is loaded", () => {
    const { text, cleanup: done } = renderWithConfig(undefined);
    cleanup = done;
    expect(text).toBe("false|false|false");
  });

  it("reflects the server flags when AI is enabled", () => {
    const { text, cleanup: done } = renderWithConfig({
      ai: {
        enabled: true,
        features: {
          policyAuthoring: true,
          decisionExplainer: true,
          impactAnalysis: true,
          configAdvisor: true,
          accessSearch: false,
          sodAnalysis: true,
          accessCertification: true,
          auditNarrative: true,
        },
      },
    });
    cleanup = done;
    expect(text).toBe("true|true|false");
  });

  it("gates every feature off when the master switch is off", () => {
    const { text, cleanup: done } = renderWithConfig({
      ai: {
        enabled: false,
        features: {
          policyAuthoring: true,
          decisionExplainer: true,
          impactAnalysis: true,
          configAdvisor: true,
          accessSearch: true,
          sodAnalysis: true,
          accessCertification: true,
          auditNarrative: true,
        },
      },
    });
    cleanup = done;
    expect(text).toBe("false|false|false");
  });
});
