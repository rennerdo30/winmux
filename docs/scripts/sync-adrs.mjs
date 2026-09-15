// Mirror docs/adr/*.md into the Starlight content collection.
//
// The ADRs live in docs/adr/ and stay there: every link in CLAUDE.md, HANDOFF.md and the ADRs
// themselves points at that path, and they are read far more often on GitHub than on the site. So
// the site copies them in at build time rather than owning them, and src/content/docs/adr/ is
// gitignored — there is exactly one editable copy of each decision.
//
// Starlight needs frontmatter with a title; the ADRs begin with an H1 instead, so the title is
// taken from that and the heading dropped to avoid rendering it twice.

import { readdir, readFile, writeFile, mkdir, rm } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, "..", "adr");
const target = join(here, "..", "src", "content", "docs", "adr");

/**
 * A YAML single-quoted scalar: the only escape is a doubled quote, so a title full of colons,
 * backticks and dashes needs no further thought. A double-quoted scalar would need backslashes,
 * which is how the first version of this produced unparseable frontmatter.
 */
const quote = (value) => `'${value.replace(/'/g, "''")}'`;

/**
 * Links between ADRs are written relative to docs/adr/ ("0016-....md"). On the site they are
 * routes, so the extension goes and the path becomes site-absolute.
 *
 * An ADR may also point at a guide page, written as the real repository path
 * ("../src/content/docs/network-shares.mdx") so that it resolves when the ADR is read on GitHub —
 * which is where they are read most. Guides are top-level routes, so the whole prefix collapses to
 * a slug.
 */
function rewriteLinks(body) {
  return body
    .replace(/\]\((\.\/)?(\d{4}-[a-z0-9-]+)\.md(#[^)]*)?\)/gi, "](/adr/$2/$3)")
    .replace(/\]\(\.\.\/src\/content\/docs\/([a-z0-9-]+)\.mdx?(#[^)]*)?\)/gi, "](/$1/$2)");
}

const files = (await readdir(source)).filter((name) => name.endsWith(".md")).sort();

await rm(target, { recursive: true, force: true });
await mkdir(target, { recursive: true });

let count = 0;
for (const name of files) {
  const raw = await readFile(join(source, name), "utf8");

  const heading = raw.match(/^#\s+(.+)$/m);
  if (!heading) {
    console.warn(`sync-adrs: ${name} has no H1, skipping`);
    continue;
  }

  const title = heading[1].trim();
  const body = rewriteLinks(raw.replace(heading[0], "").trimStart());

  // A one-line summary for the sidebar and search: the first bold "Status:" line if there is one.
  const status = raw.match(/^\*\*Status:\*\*\s*(.+)$/m);
  const description = status
    ? `Architecture decision record — ${status[1].replace(/\*\*/g, "").trim()}`
    : "Architecture decision record";

  const frontmatter = [
    "---",
    `title: ${quote(title)}`,
    `description: ${quote(description)}`,
    "---",
    "",
  ].join("\n");

  await writeFile(join(target, name), frontmatter + body, "utf8");
  count += 1;
}

console.log(`sync-adrs: mirrored ${count} ADR(s) into src/content/docs/adr/`);
