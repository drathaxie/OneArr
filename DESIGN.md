# OneArr — Concept C: Media Command Center (implementation)

Branch: `feature/command-center` (from main `11744dc`). This supersedes the
rejected milestone-1 branch `feature/premium-ui` (light restyle, never pushed);
nothing from that branch was carried over — this is a fresh implementation of
the approved Concept C refined mockup (`~/workspace/onearr-concepts/concept-c-refined.html`,
third/fullpage screenshot as primary reference).

Scope of this milestone: application shell, Movies/Series library, and the
right-side inspector. Dashboard, Wanted, Activity, Settings, and Migration are
NOT redesigned here (their components are untouched except where noted below).
No merge, publish, tag, or release without the user's approval.

## Language

- Dark-first charcoal desktop (`--bg #0e1013`, panels `#16191e`), teal
  interactive accent (`#6db5a6`), amber only for attention, red only for
  destructive actions. Light mode follows the system in warm paper with a
  darker teal (`#0f766e`) for readable accent buttons; it is not an afterthought.
- Typography hierarchy: 28px/800 page titles, 10.5px uppercase tracked section
  labels, 14px/650 card titles, 12.5px metadata. No identical-size-everywhere
  system text, no gradients, no glassmorphism, no decorative charts.
- Density is a feature, not a flaw: 8 poster columns at 1920 without the
  inspector, 6 with it, 4 at 1366 with it, 8 in light grid at 1920 — posters
  resize, never crop, and the page never scrolls horizontally.

## What changed

- `web/src/App.tsx` — grouped shell nav (Library / Operations / System) with
  icons, `aria-current`, and ops-strip navigation plumbing (Activity initial
  tab, Settings initial section). Auth form unchanged.
- `web/src/command-center.tsx` — new. `OpsStrip` (real dashboard/queue/
  problems data; hides rather than fabricates if any load fails; every segment
  is a button to the real handling page) and `InspectorPanel`.
- `web/src/pages.tsx` — `MediaPage` rewrite: command bar, poster grid with
  ••• contextual menu, workspace + inspector, list view (unchanged structure),
  native `confirm()` removal replaced by an accessible `ConfirmDialog`.
  `MediaDetail` (full detail page) is unchanged and still reachable from the
  poster's Details menu item.
- `web/src/common.tsx` — `primaryStatus`/`availabilityClass` moved here so
  grid, inspector, and detail view share one rule; `ConfirmDialog` (focus
  starts on Cancel, Escape/backdrop cancels, destructive action only on click).
- `web/src/settings.tsx` — `Settings` accepts an optional `initialSection`
  (used by the ops strip's Review scan action); default behaviour unchanged.
- `web/src/styles.css` — Command Center tokens, shell, ops strip, command bar,
  grid, inspector, and reflow rules (inspector 360px, 320px ≤1400px, stacks
  under the grid ≤900px so cards never go unusably narrow).

## The seven final refinements, as implemented

1. **One monitoring indicator per card.** Monitoring is the badge on the
   poster top-left only. Below is availability only: monitored → Available/
   Missing/Complete/Partial badge; unmonitored → secondary file state
   ("File available"/"No file", or episode counts for series). No second
   Monitored/Unmonitored pill.
2. **Editable profile and root in the inspector.** Both use the exact
   `PATCH /api/media/{id}` the detail view uses. Profile changes on select;
   root saves on submit after the Add dialog's path-shape check (drive/UNC/
   POSIX) — an invalid root is rejected locally with an inline error and
   never PATCHed. Server errors show inline and values revert on reload.
3. **Responsive density, no overflow.** Verified by measurement in the real
   app: `scrollWidth == viewport` at 1920 and 1366 with the inspector open.
4. **Keyboard shortcuts never interfere.** Arrow stepping and Escape are
   skipped when focus is in an input/textarea/select/contenteditable, when a
   poster menu is open (Escape closes the menu), or when the remove dialog is
   open (it owns Escape via a capture-phase handler).
5. **Focus management.** Opening the inspector focuses its close button;
   stepping does not remount the panel, so focus stays on ‹/›; closing
   returns focus to the poster button of the title shown at close time.
6. **Ops actions are real.** Library health → Settings → Media Management
   (scan lives there); Wanted → Wanted page; Active downloads → Activity
   queue tab; Problems → Activity problems tab. Counts are live from
   `/api/dashboard`, `/api/queue`, `/api/activity/problems`.
7. **Safety preserved.** Monitoring, import (preview + explicit confirm),
   migration, and automatic grabbing are untouched — no backend changes at
   all in this milestone. Remove is dialog-confirmed only; files/torrents are
   never touched by it. The inspector itself never navigates away from the
   library; full details are shown in place, and the full detail page remains
   available from the menu.

## Pre-existing bug found and fixed (not a redesign)

The frontend called `/activity/history` and `/activity/problems`, but the
backend registers those routes under the `/api` group, so the real routes are
`/api/activity/history` and `/api/activity/problems`. The old paths returned
the SPA `index.html` with HTTP 200, so JSON parsing failed and Activity's
History/Problems tabs never loaded — and the new ops strip hid itself for the
same reason until the path was corrected. Fixed in `pages.tsx` (Activity) and
`command-center.tsx` (OpsStrip); test stubs updated to the real routes. No
test was weakened: the same behaviours are asserted, against the correct URL.

## Verification (this milestone)

- Frontend: 50/50 tests pass (44 pre-existing + 6 new in
  `command-center.test.tsx`), `tsc --noEmit` clean, `vite build` clean.
- Backend: `dotnet build -c Release` clean, 0 warnings/errors (the Windows
  CI build runs the same build; no Windows machine was available here, so CI
  remains the authoritative Windows check).
- Real app (throwaway config + seeded TEST database only, 12 titles, real
  TMDb posters served locally because sandbox Chromium cannot reach the image
  host): inspector open at 1920 and 1366 with no overflow, light grid at
  1920, full-page inspector including Management. Live checks in the real
  app: invalid root rejected, profile changed UHD-2160p and restored to
  HD-1080p. Nothing in the test database was presented as production data,
  and no sample title or operational number is hardcoded in production code
  (the ops strip hides if any source fails).

## Visual polish pass (same milestone, second commit)

CSS-led only; no behaviour, API, keyboard, or safety change (50/50 tests
still pass, same DOM/classes so existing assertions hold):

- Cards: no border, no panel background, no rectangular metadata block —
  artwork leads, poster radius + restrained shadow, selection rings the
  poster only. Titles 15px/700, metadata 13px (larger, not smaller).
- Availability on cards/inspector: coloured dot + text instead of a bordered
  pill (badge classes kept in the DOM).
- Toolbar: uniform 38px controls, one radius/surface, custom chevron on
  selects (`appearance: none`), active view segment in accent — one
  instrument, not browser defaults.
- Inspector: larger identity (112×168 poster, 20px/800 title), sections
  separated by hairlines and spacing, files as hairline-divided rows,
  Management as the one distinct restrained panel at the foot.
- Ops strip: no boxed panel — a status line with a single hairline below,
  17px values, action as a quiet teal link. Same data, same navigation.
- Before screenshots preserved at `~/workspace/onearr-cc-visual/shots/before/`;
  updated screenshots at the same resolutions, with measured zero horizontal
  overflow at 1920 and 1366 with the inspector open.

## Refined Concept A implementation (approved 2026-10-28, this commit)

Refined Concept A is now the visual foundation — not another concept, and
not a font swap. The composition changed: ruled catalog masthead, archive
sidebar, ledger, workbench toolbar, keylined catalog cards, and the
inspector rebuilt as a numbered dossier. No backend change, no API contract
change, no grabbing change, no merge/publish (local commit only).

- Typography (bundled offline, `web/public/fonts/` + OFL texts): Fraunces
  for identity only (brand, page titles, dossier titles), IBM Plex Sans
  for all functional text, IBM Plex Mono for verifiable facts (years,
  profiles, paths, filenames, sizes, status). Fraunces/Plex Sans are
  variable fonts, declared once with weight ranges; Plex Mono is static
  Regular/Medium/SemiBold (verified from the font tables, not assumed).
  The page title is 48px (slightly compacted from the mockup's 56px) so
  more of the library fits at 1080p; nothing was shrunk for density.
- Shell: Fraunces wordmark + PERSONAL ARCHIVE mono, numbered groups
  (01 Library / 02 Operations / 03 System), counts in mono from the real
  dashboard endpoint (Movies, Series, Wanted total), active item marked
  by a teal rule. The footer shows the real automatic-grabbing state from
  /api/settings (OFF in practice) and hides if either load fails — it
  never asserts a state it has not read.
- Ledger (replaces the ops strip): one ruled row, no boxes, no expand
  control — the Dashboard already holds the detail. States differentiated:
  Healthy green, Wanted count teal, Not configured amber, problems amber,
  Idle/None grey. Download health comes from the real queue clients:
  no clients → Not configured, active jobs → N active, clients present
  with none active → Idle. All four actions navigate as before.
- Cards: keylined prints, no overlay on the art, ••• menu moved into the
  title row (same menu items, same dialog-confirmed removal). Meta line
  labels the profile explicitly ("2024 · Profile HD-1080p"). Availability
  is an outlined mono stamp; monitoring is a separate mono note — exactly
  one monitoring indicator per card. Unmonitored stamps state the file
  fact (File linked / No file), never Missing.
- Dossier: head (DOSSIER + ‹ › ×) and actions footer are fixed; the
  middle scrolls independently with the identity block sticky at its top,
  so title/profile/file facts stay visible while sections scroll and
  Search releases is always reachable in the panel's working state. The
  panel sticks on page scroll like any sticky sidebar; keyboard Tab
  brings a focused action into view automatically. Sections: 01
  Monitoring (same switch + PATCH), 02 Availability (stamp + the wanted
  consequence in plain language, plus series episode counts), 03 Quality
  & Location (same profile select + PATCH, mono profile details,
  root input + Save as one attached unit, same path-shape rejection),
  04 Files (mono table with a TOTAL row: summed bytes + file count),
  05 Full details (overview, in place). Actions: full-width Search
  releases, Files + Full details row (Full details opens the existing
  detail page), separate Remove row with the safety note and the same
  confirmation dialog wording.
- Profile vs file resolution, kept strictly apart: identity lines read
  PROFILE <configured profile> and FILE <first file's resolution · size>
  (or FILE —). Verified with a deliberately mismatched throwaway case
  (Dune: profile UHD-2160p, file 1080p) — the throwaway DB only; the user's
  real library is untouched.
- Synchronization: the dossier renders beside the list view too (a
  Dossier button per row; the list title still opens the detail page),
  so switching views never loses the selection. Keyboard stepping follows
  the filtered order in both views. When the user changes search/filter
  and the dossier's title leaves the visible list, the selection clears
  (without moving focus out of the search/filter control). Esc closes and
  returns focus to the poster button; the × button does the same, falling
  back to the opener in list view. Removal closes the dossier for the
  removed title. The Add button keeps its established accessible name
  ("Add Movie"/"Add Series").
- Responsive: verified in the real app at 1728px (4 columns + dossier),
  1366px (3 columns + dossier, ledger 2×2), and 390px (2-column grid;
  dossier as a fixed full-screen drawer below the dialog layer, so the
  Remove confirmation always renders above it). Measured zero horizontal
  overflow at all three widths. Screenshots in
  `~/workspace/onearr-cc-visual/shots/a-implemented-*.png`.
- Tests: 52/52 pass (50 existing + 2 new: filter-clears-selection,
  view-switch-keeps-dossier; plus profile-vs-file and ledger-state
  assertions folded into existing cases). Tests that asserted the old
  badge/pill presentation were updated to the stamp/note presentation;
  no test was weakened — every behaviour assertion (PATCH endpoints,
  local root rejection, dialog-confirmed-only removal, stepping guards,
  focus return) still holds. tsc clean, vite build clean, dotnet Release
  build clean (0 errors). CI remains the authoritative Windows check.

## Review refinements (2026-10-28, same branch, second commit)

Five corrections from the user's review of the implementation screenshots,
all in the app (the concept mockup is left as the historical proposal):

1. Tagline removed. The Movies/Series masthead is now just the Fraunces
   title plus the mono inventory line — the name and the interface carry
   the purpose; no manufactured branding line.
2. Archive labeling removed, both instances: the "PERSONAL ARCHIVE"
   designation under the sidebar wordmark and the "PERSONAL MEDIA
   ARCHIVE" kicker above the page title are both gone from the app. The
   sidebar is just "OneArr" now. If one instance should live somewhere
   specific, that is an open call, not a default.
3. Smallest text raised ~1px across the library UI: card meta 12→13px
   (fixed height 18→20px so two-line titles still fit), stamps
   10.5→11.5px, monitoring notes 11→12px, ledger details 11.5→12.5px,
   inventory/result counts 12→13px, dossier meta and file tables
   12.5→13px, and every 10px label (nav headings, ledger labels, dossier
   head, Sort/Filter labels, file-table headers) to 11px. Nothing in the
   library UI now sits below 11px.
4. Availability terminology standardized for movies, in one shared rule
   (common.availabilityStamp) used by both cards and dossier, so they
   cannot drift apart again: a file that exists reads "Available"
   whether the title is monitored or not — Dune and Spider-Man now say
   the same word, and monitoring appears only in the separate note.
   The no-file wording still differs on purpose: "Missing" when
   monitored (the title is wanted) vs "No file" when unmonitored
   (deliberately unwatched, never wanted) — wanted-ness is a real
   difference, not a wording drift. Series keep "Unmonitored" as the
   stamp when unmonitored: series availability is episode-based, and an
   unmonitored series must never be headlined Missing/Complete.
   Detail/list views keep their established primary-state presentation
   (Unmonitored primary) — unchanged by this commit.
5. Ledger download status precedence fixed: no clients → "Not
   configured"; all clients unreachable → "Connection failed" (amber)
   with the failed client named in the detail; some unreachable → the
   value still reflects the reachable clients, detail names the failed
   one; otherwise active count / Idle as before. A client that exists
   but failed its connection test is never again reported as "Not
   configured" or "Idle".
- Tests now 54/54 (the 2 new cases: an unmonitored movie with a file
  reads Available + one UNMONITORED note and no "File linked"; a single
  unreachable client reads Connection failed, never Not configured or
  Idle). Real-app verified on a throwaway copy of the pristine seed
  with a qb client seeded at a dead port plus a health LastError:
  Spider-Man's card reads AVAILABLE + UNMONITORED, the ledger reads
  Connection failed / qb unreachable, and Library health correctly
  drops to Needs attention while a client is unreachable. Zero
  horizontal overflow at 1728px; screenshot
  `~/workspace/onearr-cc-visual/shots/b-implemented-1728x1080.png`.

## Still outstanding (not in this milestone)

- Remaining screens (Dashboard operational sections, Wanted, Activity,
  Settings, Migration) in this language — needs approval.
- the user's real Windows PC check against his real library and qBittorrent
  (including a download client actually configured, which the throwaway
  DB cannot show as connected).
- The separate qBittorrent 5.2+ work (local-only, uncommitted) — untouched.
