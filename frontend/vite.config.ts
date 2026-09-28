import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes("node_modules")) return undefined;
          if (id.includes("d3-")) return "vendor-charts";
          if (
            id.includes("react-router") ||
            id.includes("react-dom") ||
            id.includes("/react/")
          )
            return "vendor-react";
          if (id.includes("@tanstack")) return "vendor-query";
          return undefined;
        },
      },
    },
  },
});
