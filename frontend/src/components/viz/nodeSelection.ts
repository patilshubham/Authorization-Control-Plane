// Maps a graph node (from the D3 hierarchy views) to the workspace `Selection`
// so clicking a node in any app-scoped graph navigates to the matching entity.

import type { GraphNodeDatum } from "./nodes";
import type { Selection } from "../../workspace/selection";

/** Returns a workspace Selection for role/permission/policy nodes, else null. */
export function nodeSelection(node: GraphNodeDatum): Selection | null {
  const key = node.id.slice(node.id.indexOf(":") + 1);
  switch (node.kind) {
    case "role":
      return { kind: "role", key };
    case "permission":
      return { kind: "permission", key };
    case "policy":
      return { kind: "policy", key };
    default:
      return null;
  }
}
