import { describe, expect, it } from "vitest";
import {
  deserialize,
  serialize,
  summarize,
  validate,
  emptyRoot,
  newRule,
  valueMode,
  valueKey,
  composeValue,
  type GroupNode,
} from "./model";

describe("condition model", () => {
  it("round-trips a flat AND list to the backend contract", () => {
    const json = JSON.stringify({
      match: "all",
      conditions: [
        {
          attribute: "context.amount",
          operator: "lte",
          value: "assignment.approvalLimit",
        },
        { attribute: "context.vendorRisk", operator: "neq", value: "HIGH" },
      ],
    });
    const { root, error } = deserialize(json);
    expect(error).toBeNull();
    expect(root.kind).toBe("group");
    expect(root.combinator).toBe("all");
    expect(root.children).toHaveLength(2);

    const reserialized = JSON.parse(serialize(root));
    expect(reserialized.match).toBe("all");
    expect(reserialized.conditions[0]).toEqual({
      attribute: "context.amount",
      operator: "lte",
      value: "assignment.approvalLimit",
    });
  });

  it("parses and re-emits nested any/all groups", () => {
    const json = JSON.stringify({
      match: "all",
      conditions: [
        { attribute: "context.amount", operator: "gt", value: "1000" },
        {
          match: "any",
          conditions: [
            { attribute: "context.vendorRisk", operator: "eq", value: "HIGH" },
            { attribute: "context.region", operator: "eq", value: "EU" },
          ],
        },
      ],
    });
    const { root } = deserialize(json);
    const nested = root.children[1];
    expect(nested.kind).toBe("group");
    if (nested.kind === "group") {
      expect(nested.combinator).toBe("any");
      expect(nested.children).toHaveLength(2);
    }
    const out = JSON.parse(serialize(root));
    expect(out.conditions[1].match).toBe("any");
    expect(out.conditions[1].conditions).toHaveLength(2);
  });

  it("summarizes rules in plain English with AND/OR", () => {
    const { root } = deserialize(
      JSON.stringify({
        match: "any",
        conditions: [
          { attribute: "context.region", operator: "in", value: "EU, US" },
          { attribute: "context.amount", operator: "gt", value: "1000" },
        ],
      }),
    );
    const text = summarize(root);
    expect(text).toContain("OR");
    expect(text).toContain("context.region");
    expect(text).toContain("[EU, US]");
  });

  it("flags an empty attribute and a missing value", () => {
    const root: GroupNode = {
      ...emptyRoot(),
      children: [
        { ...newRule(), attribute: "", operator: "eq", value: "x" },
        {
          ...newRule(),
          attribute: "context.amount",
          operator: "gt",
          value: "",
        },
      ],
    };
    const issues = validate(root);
    expect(issues.length).toBeGreaterThanOrEqual(2);
  });

  it("does not require a value for presence operators", () => {
    const root: GroupNode = {
      ...emptyRoot(),
      children: [
        {
          ...newRule(),
          attribute: "context.mfa",
          operator: "exists",
          value: "",
        },
      ],
    };
    expect(validate(root)).toHaveLength(0);
    const out = JSON.parse(serialize(root));
    expect(out.conditions[0].operator).toBe("exists");
  });

  it("treats empty input as an always-applies empty group", () => {
    const { root, error } = deserialize("");
    expect(error).toBeNull();
    expect(root.children).toHaveLength(0);
    expect(summarize(root)).toMatch(/always applies/i);
  });

  it("round-trips a NONE (negation) group and summarizes it as NOT", () => {
    const json = JSON.stringify({
      match: "none",
      conditions: [
        { attribute: "context.amount", operator: "gt", value: "100" },
      ],
    });
    const { root, error } = deserialize(json);
    expect(error).toBeNull();
    expect(root.combinator).toBe("none");
    expect(JSON.parse(serialize(root)).match).toBe("none");
    expect(summarize(root)).toContain("NOT");
  });

  it("accepts the new operators and system attribute source", () => {
    const root: GroupNode = {
      ...emptyRoot(),
      children: [
        {
          ...newRule(),
          attribute: "context.code",
          operator: "notContains",
          value: "x",
        },
        {
          ...newRule(),
          attribute: "context.code",
          operator: "matches",
          value: "^inv-[0-9]+$",
        },
        {
          ...newRule(),
          attribute: "context.expiry",
          operator: "after",
          value: "system.now",
        },
        {
          ...newRule(),
          attribute: "system.dayOfWeek",
          operator: "eq",
          value: "Monday",
        },
      ],
    };
    expect(validate(root)).toHaveLength(0);
  });

  it("validates the between range needs exactly two bounds", () => {
    const bad: GroupNode = {
      ...emptyRoot(),
      children: [
        {
          ...newRule(),
          attribute: "context.amount",
          operator: "between",
          value: "10",
        },
      ],
    };
    expect(validate(bad).length).toBeGreaterThanOrEqual(1);

    const good: GroupNode = {
      ...emptyRoot(),
      children: [
        {
          ...newRule(),
          attribute: "context.amount",
          operator: "between",
          value: "10, 20",
        },
      ],
    };
    expect(validate(good)).toHaveLength(0);
    expect(summarize(good)).toContain("10 … 20");
  });

  it("does not require a value for the boolean presence operators", () => {
    const root: GroupNode = {
      ...emptyRoot(),
      children: [
        {
          ...newRule(),
          attribute: "context.flag",
          operator: "isTrue",
          value: "",
        },
      ],
    };
    expect(validate(root)).toHaveLength(0);
    expect(JSON.parse(serialize(root)).conditions[0].operator).toBe("isTrue");
  });
});

describe("reference-data value mode", () => {
  it("classifies value expressions by prefix", () => {
    expect(valueMode("reference.allowed_countries")).toBe("reference");
    expect(valueMode("context.country")).toBe("context");
    expect(valueMode("assignment.limit")).toBe("assignment");
    expect(valueMode("system.now")).toBe("system");
    expect(valueMode("HIGH")).toBe("literal");
  });

  it("extracts and re-composes the reference key round-trip", () => {
    const value = composeValue("reference", "allowed_countries");
    expect(value).toBe("reference.allowed_countries");
    expect(valueMode(value)).toBe("reference");
    expect(valueKey(value)).toBe("allowed_countries");
  });

  it("supports a nested reference path", () => {
    const value = composeValue("reference", "limits.maxAmount");
    expect(value).toBe("reference.limits.maxAmount");
    expect(valueKey(value)).toBe("limits.maxAmount");
  });

  it("round-trips a reference-backed condition through the backend contract", () => {
    const json = JSON.stringify({
      match: "all",
      conditions: [
        {
          attribute: "context.country",
          operator: "in",
          value: "reference.allowed_countries",
        },
      ],
    });
    const { root, error } = deserialize(json);
    expect(error).toBeNull();
    const out = JSON.parse(serialize(root));
    expect(out.conditions[0]).toEqual({
      attribute: "context.country",
      operator: "in",
      value: "reference.allowed_countries",
    });
  });

  it("does not require literal list parsing for a reference value", () => {
    const root: GroupNode = {
      ...emptyRoot(),
      children: [
        {
          ...newRule(),
          attribute: "context.country",
          operator: "in",
          value: "reference.allowed_countries",
        },
      ],
    };
    expect(validate(root)).toHaveLength(0);
  });

  it("flags a reference value that is missing its key", () => {
    const root: GroupNode = {
      ...emptyRoot(),
      children: [
        {
          ...newRule(),
          attribute: "context.country",
          operator: "eq",
          value: "reference.",
        },
      ],
    };
    expect(validate(root).length).toBeGreaterThanOrEqual(1);
  });
});
