import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  build: {
    outDir: "../canvas",
    emptyOutDir: true,
    sourcemap: false,
    target: "es2022",
    rollupOptions: {
      onwarn(warning, warn) {
        // This is a client-only local editor, not a React server-components app.
        if (warning.code === 'MODULE_LEVEL_DIRECTIVE' && warning.message.includes('use client')) return;
        warn(warning);
      },
    },
  },
});
