/** @vitest-environment jsdom */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { AdvisorSummary } from "./ConfigAdvisor";

// Unit tests for the structured advisor-summary renderer: it turns the AI's lightweight Markdown
// (## sections, - bullets, **bold**, `code`) into scannable, safe React nodes, and degrades to a
// plain paragraph when the summary carries no structure.

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
});

describe("AdvisorSummary", () => {
  it("renders structured sections, bullets, bold and inline code", () => {
    const markdown = [
      "## Executive Summary",
      "This review surfaced **3 findings**.",
      "",
      "## Key Observations",
      "- Policy `price-policy` references a dead attribute.",
      "- A second observation.",
    ].join("\n");

    act(() => root.render(<AdvisorSummary text={markdown} />));

    const headings = Array.from(
      container.querySelectorAll("h4.advisor-sum-h"),
    ).map((heading) => heading.textContent);
    expect(headings).toEqual(["Executive Summary", "Key Observations"]);

    expect(container.querySelectorAll("li")).toHaveLength(2);
    expect(container.querySelector("strong")?.textContent).toBe("3 findings");
    expect(container.querySelector("code")?.textContent).toBe("price-policy");
  });

  it("renders plain, unstructured text as a single paragraph", () => {
    act(() => root.render(<AdvisorSummary text="Just a plain summary." />));

    const paragraphs = container.querySelectorAll("p.advisor-sum-p");
    expect(paragraphs).toHaveLength(1);
    expect(paragraphs[0].textContent).toBe("Just a plain summary.");
    expect(container.querySelectorAll("h4")).toHaveLength(0);
  });
});
