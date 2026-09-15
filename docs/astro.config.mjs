import { defineConfig } from "astro/config";
import starlight from "@astrojs/starlight";
import starlightThemeGalaxy from "starlight-theme-galaxy";
import starlightClientMermaid from "@pasqal-io/starlight-client-mermaid";

// site and base are passed on the command line in CI, from actions/configure-pages.
export default defineConfig({
  integrations: [
    starlight({
      title: "WinMux",
      description:
        "A window multiplexer for Windows: tmux-style panes, tabs and session restore for terminals and ordinary applications alike.",
      plugins: [starlightThemeGalaxy(), starlightClientMermaid()],
      social: [
        { icon: "github", label: "GitHub", href: "https://github.com/rennerdo30/winmux" },
      ],
      editLink: {
        baseUrl: "https://github.com/rennerdo30/winmux/edit/main/docs/",
      },
      sidebar: [
        { label: "What WinMux is", slug: "index" },
        { label: "Getting started", slug: "getting-started" },
        {
          label: "Using it",
          items: [
            { label: "Panes and layout", slug: "panes-and-layout" },
            { label: "Sessions", slug: "sessions" },
            { label: "Keyboard", slug: "keyboard" },
            { label: "Profiles and applications", slug: "profiles" },
            { label: "Settings", slug: "settings" },
          ],
        },
        {
          label: "Extending it",
          items: [
            { label: "Writing a pane provider", slug: "providers" },
          ],
        },
        {
          label: "Keeping it current",
          items: [
            { label: "Updates", slug: "updates" },
          ],
        },
        { label: "Troubleshooting", slug: "troubleshooting" },
        {
          label: "Architecture",
          items: [
            { label: "Overview", slug: "architecture" },
            // Mirrored from docs/adr/ by scripts/sync-adrs.mjs before every build.
            { label: "Decision records", autogenerate: { directory: "adr" } },
          ],
        },
      ],
    }),
  ],
});
