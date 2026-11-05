# Bundled fonts (Concept A — Editorial Archive)

These files are served by the app itself from `/fonts/` — no network font
loading, no system font installation. All are licensed under the SIL Open
Font License 1.1; the full license text for each family sits beside its
files and must stay with any distribution of the app.

- **Fraunces** (display serif, variable weight + italic) — `OFL-Fraunces.txt`
- **IBM Plex Sans** (interface text, variable weight) — `OFL-IBMPlexSans.txt`
- **IBM Plex Mono** (technical text: years, profiles, paths, filenames, sizes,
  status) — `OFL-IBMPlexMono.txt`

Source: Google Fonts (SIL OFL). Fraunces and Plex Sans are variable fonts,
so each is declared once with a weight range in `web/src/styles.css`;
Plex Mono ships static Regular / Medium / SemiBold files.
