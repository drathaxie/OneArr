import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { Movies } from "./pages";
import { setToken } from "./api";

// Command Center tests: the inspector, its editable fields, keyboard guards, focus
// management, the ops strip, and confirmed-only removal. The API is replaced at the
// fetch layer; no sample title or operational number here ever reaches production.

let calls: { method: string; path: string; body?: unknown }[];
let navigateCalls: { page: string; opts?: unknown }[];

const MOVIES = [
  { id: 1, mediaType: "Movie", providerId: "1", title: "Dune", year: 2021, overview: "Desert planet.", posterPath: "/dune.jpg", monitored: true, qualityProfileId: 1, rootFolder: "/data/movies", hasFile: true },
  { id: 2, mediaType: "Movie", providerId: "2", title: "Arrival", year: 2016, overview: "Visitors.", posterPath: null, monitored: false, qualityProfileId: 1, rootFolder: "/data/movies", hasFile: false },
];

function detailForMovie(m: (typeof MOVIES)[number]) {
  return {
    id: m.id, mediaType: "Movie", title: m.title, year: m.year, overview: m.overview, posterPath: m.posterPath,
    monitored: m.monitored, qualityProfileId: m.qualityProfileId, rootFolder: m.rootFolder, hasFile: m.hasFile,
    legacyPath: null, legacySource: null, seasons: [], episodes: [],
    files: m.hasFile ? [{ path: `/data/movies/${m.title.replace(/[^A-Za-z0-9]+/g, "")}.mkv`, sizeBytes: 123456, resolution: 1080, status: "Present", matchMethod: "title" }] : [],
  };
}

function detailFor(id: number) {
  return detailForMovie(MOVIES.find((x) => x.id === id)!);
}

// Variant backend state for single-case tests (an unmonitored movie with a
// file, an unreachable download client). Same routes as fakeFetch unless
// overridden; PATCH/DELETE still answer 204.
function variantFetch(overrides: { movies?: typeof MOVIES; queue?: unknown; problems?: unknown }) {
  return (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const path = String(input);
    const method = init?.method ?? "GET";
    let body: unknown;
    try { body = init?.body ? JSON.parse(String(init.body)) : undefined; } catch { body = undefined; }
    calls.push({ method, path, body });
    const movies = overrides.movies ?? MOVIES;
    const routes: Record<string, unknown> = {
      "GET /api/media?type=movie": movies,
      "GET /api/quality-profiles": [
        { id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true },
        { id: 2, name: "UHD-2160p", minResolution: 1080, cutoffResolution: 2160, allowWebSources: true },
      ],
      "GET /api/dashboard": { movies: movies.length, series: 3, wantedMovies: 1, wantedEpisodes: 2, activeDownloads: 1, tmdbConfigured: true, recentJobs: [] },
      "GET /api/queue": overrides.queue ?? { jobs: [], clients: [{ id: 1, name: "qb", enabled: true, lastSeenAt: null, lastError: null }] },
      "GET /api/activity/problems": overrides.problems ?? [
        { kind: "import-failed", title: "Import failed: X", detail: "d", jobId: 1, clientId: null, actions: ["preview"] },
        { kind: "client-unreachable", title: "Client unreachable", detail: "d", jobId: null, clientId: 1, actions: ["refresh"] },
      ],
    };
    for (const m of movies) routes[`GET /api/media/${m.id}/detail`] = detailForMovie(m);
    if (method === "PATCH" || method === "DELETE") {
      return Promise.resolve(new Response(undefined, { status: 204, headers: { "Content-Type": "application/json" } }));
    }
    const data = routes[`${method} ${path}`];
    if (data === undefined) return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${method} ${path}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
    return Promise.resolve(new Response(JSON.stringify(data), { status: 200, headers: { "Content-Type": "application/json" } }));
  };
}

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  let body: unknown;
  try { body = init?.body ? JSON.parse(String(init.body)) : undefined; } catch { body = undefined; }
  calls.push({ method, path, body });
  const routes: Record<string, unknown> = {
    "GET /api/media?type=movie": MOVIES,
    "GET /api/quality-profiles": [
      { id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true },
      { id: 2, name: "UHD-2160p", minResolution: 1080, cutoffResolution: 2160, allowWebSources: true },
    ],
    "GET /api/dashboard": { movies: 2, series: 3, wantedMovies: 1, wantedEpisodes: 2, activeDownloads: 1, tmdbConfigured: true, recentJobs: [] },
    "GET /api/queue": { jobs: [], clients: [{ id: 1, name: "qb", enabled: true, lastSeenAt: null, lastError: null }] },
    "GET /api/activity/problems": [
      { kind: "import-failed", title: "Import failed: X", detail: "d", jobId: 1, clientId: null, actions: ["preview"] },
      { kind: "client-unreachable", title: "Client unreachable", detail: "d", jobId: null, clientId: 1, actions: ["refresh"] },
    ],
    "GET /api/media/1/detail": detailFor(1),
    "GET /api/media/2/detail": detailFor(2),
    "GET /api/download-clients": [{ id: 1, name: "qBittorrent", enabled: true }],
    "POST /api/search": {
      results: [],
      indexers: [{ id: 7, name: "TorrentLeech", status: "failed", resultCount: 0, message: "HTTP 404" }],
    },
  };
  if (method === "PATCH" || method === "DELETE") {
    return Promise.resolve(new Response(undefined, { status: 204, headers: { "Content-Type": "application/json" } }));
  }
  const data = routes[`${method} ${path}`];
  if (data === undefined) return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${method} ${path}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
  return Promise.resolve(new Response(JSON.stringify(data), { status: 200, headers: { "Content-Type": "application/json" } }));
}

beforeEach(() => {
  calls = [];
  navigateCalls = [];
  localStorage.clear();
  setToken("test-session-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setToken(null);
});

function cardFor(title: string): HTMLElement {
  for (const m of screen.getAllByText(title)) {
    const card = m.closest("article");
    if (card) return card as HTMLElement;
  }
  throw new Error(`no poster card for ${title}`);
}

async function openInspector(title: string) {
  render(<Movies onNavigate={(page, opts) => { navigateCalls.push({ page, opts }); }} />);
  await screen.findByText("Showing 2 of 2.");
  fireEvent.click(screen.getByRole("button", { name: `Open ${title}` }));
  await screen.findByRole("complementary", { name: `Dossier for ${title}` });
}

describe("command center inspector", () => {
  it("shows exactly one monitoring indicator per poster card, with an availability stamp", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    const dune = within(cardFor("Dune"));
    // Refined Concept A: availability is an outlined mono stamp, monitoring a
    // separate mono note — exactly one monitoring note per card, and no old
    // sentence-case badge anywhere on the card.
    expect(dune.getAllByText("MONITORED").length).toBe(1);
    // Stamp text in the DOM is sentence case; the small-caps look is CSS.
    expect(dune.getByText("Available", { selector: "span" })).toBeTruthy();
    expect(dune.queryByText("Monitored")).toBeNull();
    const arrival = within(cardFor("Arrival"));
    expect(arrival.getAllByText("UNMONITORED").length).toBe(1);
    expect(arrival.getByText("No file", { selector: "span" })).toBeTruthy();
    // The profile is labelled as a profile on the card, never as a file resolution.
    expect(dune.getByText(/Profile HD-1080p/)).toBeTruthy();
  });

  it("opens the dossier with profile, root, linked files, and in-place details, and edits profile and root", async () => {
    await openInspector("Dune");
    const dossier = screen.getByRole("complementary", { name: "Dossier for Dune" });
    expect(within(dossier).getByText("FILES")).toBeTruthy();
    expect(screen.getByText("Dune.mkv")).toBeTruthy();
    expect(screen.getByText("Desert planet.")).toBeTruthy();
    // Profile vs file resolution are labelled apart in the identity block.
    expect(screen.getByText(/PROFILE\s+HD-1080p/)).toBeTruthy();
    expect(screen.getByText(/FILE\s+1080p/)).toBeTruthy();
    // The files table carries a total row.
    expect(screen.getByText("TOTAL")).toBeTruthy();
    // Profile edit: changing the select PATCHes the same endpoint the detail view uses.
    fireEvent.change(screen.getByLabelText("Quality profile"), { target: { value: "2" } });
    await waitFor(() => expect(calls.some((c) => c.method === "PATCH" && c.path === "/api/media/1" && (c.body as { qualityProfileId?: number }).qualityProfileId === 2)).toBe(true));
    // Root edit: an invalid path is rejected locally and never PATCHed.
    fireEvent.change(screen.getByLabelText("Root folder"), { target: { value: "not-a-path" } });
    fireEvent.click(screen.getByRole("button", { name: "Save root folder" }));
    expect(screen.getByText(/Enter a folder path/)).toBeTruthy();
    expect(calls.filter((c) => c.method === "PATCH" && c.path === "/api/media/1").length).toBe(1);
    // A valid path PATCHes.
    fireEvent.change(screen.getByLabelText("Root folder"), { target: { value: "/data/movies2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save root folder" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PATCH" && c.path === "/api/media/1" && (c.body as { rootFolder?: string }).rootFolder === "/data/movies2")).toBe(true));
  });

  it("opens the release search in a visible dialog without closing the dossier, and starts the search", async () => {
    await openInspector("Dune");
    const dossier = screen.getByRole("complementary", { name: "Dossier for Dune" });
    const open = within(dossier).getByRole("button", { name: "Search releases" });
    fireEvent.click(open);

    const dialog = await screen.findByRole("dialog", { name: "Manual release search" });
    expect(within(dialog).getByText("Manual release search")).toBeTruthy();
    expect(screen.getByRole("complementary", { name: "Dossier for Dune" })).toBeTruthy();
    await waitFor(() => expect(calls.some(c => c.method === "POST" && c.path === "/api/search" &&
      (c.body as { mediaItemId?: number } | undefined)?.mediaItemId === 1)).toBe(true));
    // A failed tracker search must remain visible, not vanish below the poster grid.
    expect(await within(dialog).findByText(/TorrentLeech: failed — HTTP 404/)).toBeTruthy();

    fireEvent.keyDown(dialog, { key: "Escape" });
    expect(screen.queryByRole("dialog", { name: "Manual release search" })).toBeNull();
    expect(screen.getByRole("complementary", { name: "Dossier for Dune" })).toBeTruthy();
    await waitFor(() => expect(document.activeElement).toBe(open));
  });

  it("opens the same visible search dialog from the poster action menu", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    fireEvent.click(screen.getByRole("button", { name: "Actions for Arrival" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Search releases" }));
    const dialog = await screen.findByRole("dialog", { name: "Manual release search" });
    expect(within(dialog).getByText("Manual release search")).toBeTruthy();
    await waitFor(() => expect(calls.some(c => c.method === "POST" && c.path === "/api/search" &&
      (c.body as { mediaItemId?: number } | undefined)?.mediaItemId === 2)).toBe(true));
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog", { name: "Manual release search" })).toBeNull();
  });

  it("keeps the release search dialog visible in List view", async () => {
    await openInspector("Dune");
    fireEvent.click(screen.getByRole("button", { name: "List" }));
    fireEvent.click(within(screen.getByRole("complementary", { name: "Dossier for Dune" }))
      .getByRole("button", { name: "Search releases" }));
    expect(await screen.findByRole("dialog", { name: "Manual release search" })).toBeTruthy();
    expect(screen.getByRole("complementary", { name: "Dossier for Dune" })).toBeTruthy();
  });

  it("toggles monitoring from the inspector switch", async () => {
    await openInspector("Arrival");
    const sw = screen.getByRole("switch", { name: "Monitored" });
    expect(sw.getAttribute("aria-checked")).toBe("false");
    fireEvent.click(sw);
    await waitFor(() => expect(calls.some((c) => c.method === "PATCH" && c.path === "/api/media/2" && (c.body as { monitored?: boolean }).monitored === true)).toBe(true));
  });

  it("steps with arrow keys, ignores arrows and Escape inside text inputs, and returns focus on close", async () => {
    await openInspector("Dune");
    const opener = screen.getByRole("button", { name: "Open Dune" });
    // ArrowLeft steps to Arrival (visible order is title-sorted: Arrival, Dune).
    fireEvent.keyDown(document, { key: "ArrowLeft" });
    await screen.findByRole("complementary", { name: "Dossier for Arrival" });
    // Inside the search input, arrows and Escape must not step or close.
    const search = screen.getByLabelText(/Search Movies/);
    (search as HTMLElement).focus();
    fireEvent.keyDown(search, { key: "ArrowRight" });
    fireEvent.keyDown(search, { key: "Escape" });
    expect(screen.getByRole("complementary", { name: "Dossier for Arrival" })).toBeTruthy();
    // Escape outside any input closes and returns focus to the opener.
    (document.activeElement as HTMLElement | null)?.blur();
    fireEvent.keyDown(document, { key: "Escape" });
    await waitFor(() => expect(screen.queryByRole("complementary")).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("button", { name: "Open Arrival" })));
    expect(opener).toBeTruthy();
  });

  it("clears the dossier selection when filtering removes its title, without stealing search focus", async () => {
    await openInspector("Dune");
    const search = screen.getByLabelText(/Search Movies/);
    (search as HTMLElement).focus();
    fireEvent.change(search, { target: { value: "arrival" } });
    await waitFor(() => expect(screen.queryByRole("complementary")).toBeNull());
    expect(document.activeElement).toBe(search);
    expect(screen.queryByText("Dune")).toBeNull();
  });

  it("keeps the dossier open and synchronized when switching to List view", async () => {
    await openInspector("Dune");
    fireEvent.click(screen.getByRole("button", { name: "List" }));
    await waitFor(() => expect(screen.getByRole("table")).toBeTruthy());
    expect(screen.getByRole("complementary", { name: "Dossier for Dune" })).toBeTruthy();
    // The selected row is highlighted in the list (the title button, not the
    // dossier's heading, is the element inside the row).
    const row = screen.getByRole("button", { name: "Dune" }).closest("tr");
    expect(row?.className).toContain("selected");
    // And switching back restores the poster grid with the same dossier.
    fireEvent.click(screen.getByRole("button", { name: "Posters" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Open Dune" })).toBeTruthy());
    expect(screen.getByRole("complementary", { name: "Dossier for Dune" })).toBeTruthy();
  });

  it("removes only after dialog confirmation, never on cancel", async () => {
    await openInspector("Dune");
    fireEvent.click(screen.getByRole("button", { name: "Remove title…" }));
    await screen.findByRole("dialog", { name: "Remove title?" });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Remove title…" }));
    await screen.findByRole("dialog", { name: "Remove title?" });
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/api/media/1")).toBe(true));
    // Confirming removal also closes the dossier for the removed title.
    await waitFor(() => expect(screen.queryByRole("complementary")).toBeNull());
  });

  it("renders the ops ledger from real endpoints, distinguishes states, and navigates its actions", async () => {
    render(<Movies onNavigate={(page, opts) => { navigateCalls.push({ page, opts }); }} />);
    await screen.findByText("View Wanted ›");
    expect(screen.getByText("3 items")).toBeTruthy();
    expect(screen.getByText("1 import failed · 1 client unreachable")).toBeTruthy();
    // One active download with a reachable client reads as active, and problems
    // present read as needing attention (amber), not as a healthy state.
    expect(screen.getByText("1 active")).toBeTruthy();
    expect(screen.getByText("2 need attention")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /View Wanted/ }));
    expect(navigateCalls[0]).toEqual({ page: "wanted", opts: undefined });
    fireEvent.click(screen.getByRole("button", { name: /Review problems/ }));
    expect(navigateCalls[1]).toEqual({ page: "activity", opts: { activityTab: "problems" } });
    fireEvent.click(screen.getByRole("button", { name: /Review scan/ }));
    expect(navigateCalls[2]).toEqual({ page: "settings", opts: { settingsSection: "media" } });
  });

  it("reads Available for an unmonitored movie with a file, with monitoring only in the note", async () => {
    const spider: (typeof MOVIES)[number] = { id: 3, mediaType: "Movie", providerId: "3", title: "Spider", year: 2023, overview: "Webs.", posterPath: null, monitored: false, qualityProfileId: 1, rootFolder: "/data/movies", hasFile: true };
    vi.stubGlobal("fetch", variantFetch({ movies: [...MOVIES, spider] }));
    render(<Movies />);
    await screen.findByText("Showing 3 of 3.");
    const card = within(cardFor("Spider"));
    // Same meaning as Dune's stamp (a file exists), same word — the note is
    // the only place monitoring appears on the card.
    expect(card.getByText("Available", { selector: "span" })).toBeTruthy();
    expect(card.getAllByText("UNMONITORED").length).toBe(1);
    expect(card.queryByText("File linked")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Open Spider" }));
    await screen.findByRole("complementary", { name: "Dossier for Spider" });
    expect(screen.getByText(/FILE\s+1080p/)).toBeTruthy();
  });

  it("reports Connection failed, not Not configured, when the only client is unreachable", async () => {
    vi.stubGlobal("fetch", variantFetch({
      queue: { jobs: [], clients: [{ id: 1, name: "qb", enabled: true, lastSeenAt: null, lastError: "Connection refused" }] },
      problems: [],
    }));
    render(<Movies />);
    await screen.findByText("Connection failed");
    // The client exists, so "Not configured" would be false; and nothing is
    // known to be idle while the only client cannot be reached.
    expect(screen.queryByText("Not configured")).toBeNull();
    expect(screen.queryByText("Idle")).toBeNull();
    expect(screen.getByText("qb unreachable")).toBeTruthy();
  });
});
