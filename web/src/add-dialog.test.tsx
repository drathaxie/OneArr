import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Series } from "./pages";
import { setToken } from "./api";

// Add Series dialog tests: no permanent add panel on the page, per-type defaults prefilled,
// explicit confirmation before adding, and the movie default never leaking into a series add.

let posts: { path: string; body?: Record<string, unknown> }[];

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  if (method === "POST" && path === "/api/media") posts.push({ path, body: init?.body ? JSON.parse(String(init.body)) : undefined });
  const routes: Record<string, unknown> = {
    "GET /api/media?type=series": [],
    "GET /api/quality-profiles": [
      { id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true },
      { id: 2, name: "HD-2160p", minResolution: 1080, cutoffResolution: 2160, allowWebSources: true },
    ],
    "GET /api/settings": { tmdbConfigured: true, dryRunImport: true, torrentRoot: "/data/torrents", mediaRoot: "/data/media", automaticGrabbing: false, defaultMovieRoot: "/data/movies", defaultSeriesRoot: "/data/tv", defaultMovieProfileId: 1, defaultSeriesProfileId: 2 },
    "GET /api/metadata/search?type=series&q=severance": [{ providerId: "222", title: "Severance", year: 2022, overview: "Office mystery." }],
    "POST /api/media": { created: true, item: { id: 9 } },
  };
  const key = `${method} ${path}`;
  const body = routes[key];
  if (body === undefined) return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${key}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }));
}

beforeEach(() => {
  posts = [];
  localStorage.clear();
  setToken("test-session-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setToken(null);
});

describe("add series dialog", () => {
  it("has no permanent add panel, opens a dialog, prefills series defaults, and confirms before adding", async () => {
    render(<Series />);
    await screen.findByText("Showing 0 of 0.");
    expect(screen.queryByLabelText("Search TMDb")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Add Series" }));
    await screen.findByRole("dialog", { name: "Add Series" });
    // Series defaults, not the movie defaults: /data/tv and profile 2 (HD-2160p).
    expect((screen.getByLabelText("Root folder") as HTMLInputElement).value).toBe("/data/tv");
    expect((screen.getByLabelText("Quality profile") as HTMLSelectElement).value).toBe("2");

    fireEvent.change(screen.getByLabelText("Search TMDb"), { target: { value: "severance" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await screen.findByText(/Severance/);
    // Nothing is added from the search list alone: adding requires selecting, then confirming.
    expect(posts).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    await screen.findByText(/Confirm before adding/);
    // HD-2160p appears in the profile select and again in the confirmation summary.
    expect(screen.getAllByText(/HD-2160p/).length).toBe(2);
    fireEvent.click(screen.getByRole("button", { name: "Add Severance" }));

    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0].body).toEqual({
      type: "series", providerId: "222", qualityProfileId: 2, rootFolder: "/data/tv",
      monitored: true, seasons: null, allSeasons: true,
    });
  });

  it("first-season monitoring scope sends only season 1", async () => {
    render(<Series />);
    await screen.findByText("Showing 0 of 0.");
    fireEvent.click(screen.getByRole("button", { name: "Add Series" }));
    await screen.findByRole("dialog", { name: "Add Series" });
    fireEvent.change(screen.getByLabelText("Search TMDb"), { target: { value: "severance" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await screen.findByText(/Severance/);
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    await screen.findByText(/Confirm before adding/);
    fireEvent.change(screen.getByLabelText("Monitor scope"), { target: { value: "first" } });
    fireEvent.click(screen.getByRole("button", { name: "Add Severance" }));
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0].body!.seasons).toEqual([1]);
    expect(posts[0].body!.allSeasons).toBe(false);
    expect(posts[0].body!.monitored).toBe(true);
  });
});
