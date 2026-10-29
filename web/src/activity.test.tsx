import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Activity } from "./pages";
import { setToken } from "./api";

// Activity tests: the Queue tab shows real client-backed rows (client, speed, ETA, import
// readiness) with per-job actions, never invented progress; Problems surfaces blockers with
// safe actions; the import preview dialog opens and applies only on confirmation.

const queue = {
  jobs: [
    {
      id: 1, mediaItemId: 10, episodeId: null, mediaType: "Series", releaseTitle: "Show S01 Complete",
      hash: "aa01", category: "onearr-tv", downloadClientId: 1, clientName: "qbit", status: "Completed",
      progress: 1, clientState: "uploading", downloadSpeedBps: 0, etaSeconds: -1,
      contentPath: "/data/torrents/Show", error: null, importFailed: false, importReady: true,
      lastSeenAt: null, completedAt: null, createdAt: "2026-10-01T12:00:00Z", updatedAt: "2026-10-01T12:00:00Z",
    },
    {
      id: 2, mediaItemId: 11, episodeId: 5, mediaType: "Series", releaseTitle: "Show S01E02",
      hash: "bb02", category: "onearr-tv", downloadClientId: 1, clientName: "qbit", status: "Downloading",
      progress: 0.5, clientState: "downloading", downloadSpeedBps: 2_500_000, etaSeconds: 600,
      contentPath: null, error: null, importFailed: false, importReady: false,
      lastSeenAt: null, completedAt: null, createdAt: "2026-10-01T12:00:00Z", updatedAt: "2026-10-01T12:00:00Z",
    },
    {
      id: 3, mediaItemId: 12, episodeId: null, mediaType: "Movie", releaseTitle: "Dune",
      hash: "cc03", category: "onearr-movies", downloadClientId: 1, clientName: "qbit", status: "Failed",
      progress: 1, clientState: "uploading", downloadSpeedBps: 0, etaSeconds: -1,
      contentPath: "/data/torrents/Dune", error: "One or more files failed to import", importFailed: true, importReady: false,
      lastSeenAt: null, completedAt: null, createdAt: "2026-10-01T12:00:00Z", updatedAt: "2026-10-01T12:00:00Z",
    },
  ],
  clients: [{ id: 1, name: "qbit", enabled: true, lastSeenAt: null, lastError: null }],
};

const history = [
  { at: "2026-10-01T12:05:00Z", kind: "grabbed", title: "Grabbed Show S01 Complete", jobId: 1 },
  { at: "2026-10-01T12:10:00Z", kind: "completed", title: "Download completed: Show S01 Complete", jobId: 1 },
];

const problems = [
  { kind: "import-failed", title: "Import failed: Dune", detail: "See import records", jobId: 3, clientId: 1, actions: ["preview", "retry"] },
];

let calls: { method: string; path: string; body?: unknown }[];
let previewPlans: unknown[];

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  calls.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : undefined });
  const routes: Record<string, unknown> = {
    "GET /api/queue": queue,
    "GET /api/settings": { automaticImport: false, dryRunImport: true },
    "GET /api/activity/history": history,
    "GET /api/activity/problems": problems,
    "GET /api/imports": [],
    "POST /api/queue/1/import-preview": { previewId: 7, jobId: 1, createdAt: "2026-10-01T12:11:00Z", plans: previewPlans, fingerprint: "abc" },
    "POST /api/queue/1/import-apply": { previewId: 7, jobId: 1, createdAt: "2026-10-01T12:11:00Z", plans: previewPlans, fingerprint: "abc" },
  };
  const body = routes[`${method} ${path}`];
  if (body === undefined) return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${method} ${path}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }));
}

beforeEach(() => {
  calls = [];
  previewPlans = [
    { sourcePath: "/data/torrents/Show/Show - S01E01.mkv", destinationPath: "/data/media/tv/Show/Season 01/Show - S01E01.mkv", sizeBytes: 100, lastWriteUtcTicks: 1, mediaType: ".mkv", plannedAction: "hardlink", reason: "Hardlink on the same volume", destinationConflict: false, sameVolume: true, matchedEpisodeIds: [5], matchedEpisodeLabels: ["S01E01"] },
  ];
  localStorage.clear();
  setToken("test-session-token");
  vi.stubGlobal("fetch", fakeFetch);
  vi.stubGlobal("confirm", () => true);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setToken(null);
});

describe("activity", () => {
  it("shows real client, speed, ETA, and import readiness without inventing values", async () => {
    render(<Activity />);
    await screen.findByText("Show S01 Complete");
    expect(screen.getAllByText("qbit").length).toBeGreaterThan(0);
    expect(screen.getByText("Ready to import")).toBeTruthy();
    expect(screen.getByText(/Downloading or seeding a torrent does not make its movie or episode available/)).toBeTruthy();
    expect(await screen.findByText(/Automatic completed-download importing is OFF/)).toBeTruthy();
    expect(screen.getByText("2.5 MB/s")).toBeTruthy();
    expect(screen.getByText("10m")).toBeTruthy();
    // The completed job reports no speed/ETA: dashes, not invented numbers.
    expect(screen.getAllByText("—").length).toBeGreaterThan(0);
    expect(screen.getByText("Failed (import failed)")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Preview import" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Retry import" })).toBeTruthy();
  });

  it("opens the preview dialog and applies only with an explicit confirmation", async () => {
    render(<Activity />);
    fireEvent.click(await screen.findByRole("button", { name: "Preview import" }));
    await screen.findByRole("dialog", { name: "Import preview" });
    expect(screen.getByText("Show - S01E01.mkv")).toBeTruthy();
    expect(screen.getByText(/hardlink/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /Apply import/ }));
    await waitFor(() => {
      expect(calls.some((c) => c.method === "POST" && c.path === "/api/queue/1/import-apply" && (c.body as { confirmed?: boolean }).confirmed === true)).toBe(true);
    });
  });

  it("lists history and problems with safe actions", async () => {
    render(<Activity />);
    fireEvent.click(await screen.findByRole("button", { name: /History/ }));
    await screen.findByText("Download completed: Show S01 Complete");
    fireEvent.click(screen.getByRole("button", { name: /Problems/ }));
    await screen.findByText("Import failed: Dune");
    expect(screen.getByRole("button", { name: "Retry failed import" })).toBeTruthy();
  });
});
