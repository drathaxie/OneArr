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

describe("direct media manager", () => {
  it("keeps poster availability distinct from monitoring", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    const dune = within(cardFor("Dune"));
    expect(dune.getAllByText("MONITORED")).toHaveLength(1);
    expect(dune.getByText("Available", { selector: "span" })).toBeTruthy();
  });

  it("opens the full modal directly from the poster with synopsis and files", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    fireEvent.click(screen.getByRole("button", { name: "Manage Dune" }));
    const dialog = await screen.findByRole("dialog", { name: "Media manager" });
    expect(within(dialog).getByText("Desert planet.")).toBeTruthy();
    expect(within(dialog).getByText("Linked files (1)")).toBeTruthy();
    fireEvent.click(within(dialog).getByRole("button", { name: "Close media manager" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Media manager" })).toBeNull());
  });

  it("opens manager from the title and manages profile and root", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    fireEvent.click(within(cardFor("Dune")).getByRole("button", { name: "Dune" }));
    const dialog = await screen.findByRole("dialog", { name: "Media manager" });
    fireEvent.change(within(dialog).getByLabelText("Quality profile"), { target: { value: "2" } });
    await waitFor(() => expect(calls.some(c => c.method === "PATCH" && c.path === "/api/media/1" && (c.body as { qualityProfileId?: number }).qualityProfileId === 2)).toBe(true));
    fireEvent.change(within(dialog).getByLabelText("Root folder"), { target: { value: "/data/movies2" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save root folder" }));
    await waitFor(() => expect(calls.some(c => c.method === "PATCH" && c.path === "/api/media/1" && (c.body as { rootFolder?: string }).rootFolder === "/data/movies2")).toBe(true));
  });

  it("preserves the operations strip navigation", async () => {
    render(<Movies onNavigate={(page, opts) => navigateCalls.push({ page, opts })} />);
    await screen.findByText("View Wanted ›");
    fireEvent.click(screen.getByRole("button", { name: /View Wanted/ }));
    expect(navigateCalls[0]).toEqual({ page: "wanted", opts: undefined });
  });

  it("reports a failed client connection without claiming idle", async () => {
    vi.stubGlobal("fetch", variantFetch({
      queue: { jobs: [], clients: [{ id: 1, name: "qb", enabled: true, lastSeenAt: null, lastError: "Connection refused" }] },
      problems: [],
    }));
    render(<Movies />);
    await screen.findByText("Connection failed");
    expect(screen.queryByText("Idle")).toBeNull();
    expect(screen.getByText("qb unreachable")).toBeTruthy();
  });
});
