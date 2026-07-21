import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Build output goes straight into wwwroot — the folder the bot's csproj copies next to the
// exe and the embedded HttpListener serves. Dev mode proxies API + WS to the live bot.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "wwwroot",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/api": "http://localhost:5666",
      "/ws": { target: "ws://localhost:5666", ws: true },
    },
  },
  preview: {
    proxy: {
      "/api": "http://localhost:5666",
      "/ws": { target: "ws://localhost:5666", ws: true },
    },
  },
});
