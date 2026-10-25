import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Production output is served by ASP.NET Core from wwwroot. Dev server proxies API calls to the backend.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/OneArr.Api/wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: { "/api": "http://localhost:8780", "/health": "http://localhost:8780" },
  },
});
