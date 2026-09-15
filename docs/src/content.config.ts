import { defineCollection } from "astro:content";
import { docsLoader } from "@astrojs/starlight/loaders";
import { docsSchema } from "@astrojs/starlight/schema";

// Declared rather than auto-generated: Astro deprecates the implicit version, and the ADRs are
// mirrored into this collection by scripts/sync-adrs.mjs before every build.
export const collections = {
  docs: defineCollection({ loader: docsLoader(), schema: docsSchema() }),
};
