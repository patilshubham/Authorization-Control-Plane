/** @vitest-environment jsdom */
import { act, useState } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { ObligationsEditor } from "./ObligationsEditor";

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

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

// The editor is fully controlled, so tests drive it through a stateful harness
// that feeds each emitted value back in as the next `value` prop, exactly like
// the real forms do. The latest emitted JSON is captured for assertions.
function renderControlled(initial: string): { latest: () => string } {
  let latest = initial;
  function Harness() {
    const [value, setValue] = useState(initial);
    return (
      <ObligationsEditor
        value={value}
        onChange={(json) => {
          latest = json;
          setValue(json);
        }}
      />
    );
  }
  act(() => {
    root.render(<Harness />);
  });
  return { latest: () => latest };
}

// Fire an input event the way React's synthetic system expects (native setter + event).
function setInputValue(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLInputElement.prototype,
    "value",
  )?.set;
  setter?.call(input, value);
  act(() => {
    input.dispatchEvent(new Event("input", { bubbles: true }));
  });
}

function click(el: Element) {
  act(() => {
    el.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  });
}

describe("ObligationsEditor", () => {
  it("shows an empty state when there are no obligations", () => {
    renderControlled("[]");
    expect(container.querySelector(".obligations-empty")).toBeTruthy();
    expect(container.querySelectorAll(".obligation-row")).toHaveLength(0);
  });

  it("renders a row per obligation from bare-string and object forms", () => {
    renderControlled('["require_mfa",{"id":"mask_ssn","value":"last4"}]');
    const rows = container.querySelectorAll(".obligation-row");
    expect(rows).toHaveLength(2);
    const ids = Array.from(
      container.querySelectorAll<HTMLInputElement>(".obligation-id"),
    ).map((i) => i.value);
    expect(ids).toEqual(["require_mfa", "mask_ssn"]);
  });

  it("serializes a value-less row to a bare string id", () => {
    const { latest } = renderControlled("[]");
    click(container.querySelector(".mini-btn")!);
    const idInput = container.querySelector<HTMLInputElement>(".obligation-id")!;
    setInputValue(idInput, "require_mfa");
    expect(JSON.parse(latest())).toEqual(["require_mfa"]);
  });

  it("serializes a row with a value to an object", () => {
    const { latest } = renderControlled('["mask_ssn"]');
    const valueInput = container.querySelector<HTMLInputElement>(
      ".obligation-value",
    )!;
    setInputValue(valueInput, "last4");
    expect(JSON.parse(latest())).toEqual([{ id: "mask_ssn", value: "last4" }]);
  });

  it("flags duplicate obligation ids", () => {
    renderControlled('["require_mfa","require_mfa"]');
    expect(container.querySelector(".obligations-error")).toBeTruthy();
    expect(
      container.querySelectorAll(".obligation-id.has-error").length,
    ).toBeGreaterThan(0);
  });

  it("ignores malformed JSON and renders the empty state", () => {
    renderControlled("not-json");
    expect(container.querySelector(".obligations-empty")).toBeTruthy();
  });
});
