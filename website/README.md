# winmux.renner.dev

The project homepage. Plain static files: no build step, no external fonts or scripts, and a
Content-Security-Policy that allows only this origin plus one request to the GitHub releases API.

- **The window in the hero is an illustration, not a screenshot.** `site.js` keeps a small model of
  WinMux's layout tree and draws it, and writes the session file the real app would write for it,
  using the key names from `WinMux.Core/Session/TomlNames.cs`. Keep the page's label saying so.
  Without JavaScript a static three-pane layout stays in place.
- **Every figure quotes a measurement.** The isolation numbers come from ADR 0001 and ADR 0017, the
  working-directory score from ADR 0004. Change them together with the ADR, never on their own.
- `releases.js` reads `/releases/latest` (stable only, like About WinMux's default channel) and
  points the buttons at that release's setup, zip and `checksums.txt`. Without it they go to the
  releases page.
- Deploy: the **Deploy Documentation** workflow builds `docs/` with base `/docs`, copies this folder
  to the root of the Pages artifact, and publishes both. The custom domain `winmux.renner.dev` needs
  its DNS record and the Pages custom-domain setting before `CNAME` takes effect.
