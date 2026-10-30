import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Movies } from "./pages";
import { setToken } from "./api";

// Poster-grid and detail-view tests. The API is replaced at the fetch layer.

let calls: { method: string; path: string }[];

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  calls.push({ method, path });
  const routes: Record<string, unknown> = {
    "GET /api/media?type=movie": [
      { id: 1, mediaType: "Movie", providerId: "1", title: "Dune", year: 2021, overview: "Desert planet.", posterPath: "/dune.jpg", monitored: true, qualityProfileId: 1, rootFolder: "/data/movies", hasFile: true },
      { id: 2, mediaType: "Movie", providerId: "2", title: "Arrival", year: 2016, overview: "Visitors.", posterPath: null, monitored: false, qualityProfileId: 1, rootFolder: "/data/movies", hasFile: false },
    ],
    "GET /api/media?type=series": [],
    "GET /api/quality-profiles": [{ id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true }],
    "GET /api/library/mapping-suggestions": { rootFolders: [], suggestions: [] },
    "GET /api/media/1/detail": {
      id: 1, mediaType: "Movie", title: "Dune", year: 2021, overview: "Desert planet.", posterPath: "/dune.jpg", monitored: true,
      qualityProfileId: 1, rootFolder: "/data/movies", hasFile: true, legacyPath: null, legacySource: null, seasons: [], episodes: [],
      files: [{ path: "/data/movies/Dune.mkv", sizeBytes: 123456, resolution: 1080, status: "Present", matchMethod: "title" }],
    },
  };
  const body = routes[`${method} ${path}`];
  if (body === undefined) return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${method} ${path}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }));
}

beforeEach(() => {
  calls = [];
  localStorage.clear();
  setToken("test-session-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setToken(null);
});

describe("poster grid", () => {
  it("shows posters by default with availability stamps and monitoring notes", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    expect(screen.getByText("Dune")).toBeTruthy();
    // Arrival appears twice: once in its poster fallback and once in its card title.
    expect(screen.getAllByText("Arrival").length).toBe(2);
    // Refined Concept A: availability is a stamp, not a badge; exactly one
    // available stamp on the page (Dune), and no red badge anywhere.
    expect(document.querySelectorAll("span.cc-stamp.ok").length).toBe(1);
    expect(document.querySelectorAll("span.badge.bad").length).toBe(0);
    // Arrival is unmonitored, so its stamp states the file fact ("No file"),
    // not Missing, and monitoring is a separate mono note per card.
    expect(screen.getByText("No file", { selector: "span" })).toBeTruthy();
    expect(screen.getAllByText("MONITORED").length).toBe(1);
    expect(screen.getAllByText("UNMONITORED").length).toBe(1);
    // Dune has a poster image; Arrival has none and gets the title fallback.
    expect(document.querySelector('img[alt="Dune poster"]')).toBeTruthy();
  });

  it("search narrows the grid and the list toggle switches views", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    fireEvent.change(screen.getByLabelText(/Search Movies/), { target: { value: "dune" } });
    await screen.findByText("Showing 1 of 2.");
    expect(screen.queryByText("Arrival")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "List" }));
    await waitFor(() => expect(screen.getByRole("table")).toBeTruthy());
  });

  it("opening a card shows the media manager with linked files", async () => {
    render(<Movies />);
    await screen.findByText("Showing 2 of 2.");
    fireEvent.click(screen.getByRole("button", { name: "Manage Dune" }));
    const dialog = await screen.findByRole("dialog", { name: "Media manager" });
    expect(dialog).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Linked files (1)" }));
    expect(screen.getByText(/Dune\.mkv/)).toBeTruthy();
    expect(calls.some((c) => c.path === "/api/media/1/detail")).toBe(true);
  });
});
