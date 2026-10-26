import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { Series } from "./pages";
import { setToken } from "./api";

// Series availability badges: the badge must come from the server's episode-based availability,
// never from the series-level HasFile flag, and counts must be shown per series.

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  const routes: Record<string, unknown> = {
    "GET /api/media?type=series": [
      { id: 1, mediaType: "Series", providerId: "1", title: "Complete Show", year: 2020, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: true, availability: "Complete", availableEpisodes: 180, applicableEpisodes: 180 },
      { id: 2, mediaType: "Series", providerId: "2", title: "Partial Show", year: 2021, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: true, availability: "Partial", availableEpisodes: 126, applicableEpisodes: 180 },
      { id: 3, mediaType: "Series", providerId: "3", title: "Missing Show", year: 2022, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: false, availability: "Missing", availableEpisodes: 0, applicableEpisodes: 40 },
      { id: 4, mediaType: "Series", providerId: "4", title: "Unknown Show", year: 2023, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: false, availability: "Unknown", availableEpisodes: 0, applicableEpisodes: 0 },
    ],
    "GET /api/quality-profiles": [{ id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true }],
  };
  const body = routes[`${method} ${path}`];
  if (body === undefined) return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${method} ${path}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }));
}

beforeEach(() => {
  localStorage.clear();
  setToken("test-session-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setToken(null);
});

describe("series availability badges", () => {
  it("shows Complete, Partial, Missing, and Unknown from episode data, not HasFile", async () => {
    render(<Series />);
    await screen.findByText("Showing 4 of 4.");
    // Partial Show has a file (HasFile true) but must NOT be labelled Complete or Available.
    expect(screen.getByText("Complete", { selector: "span" })).toBeTruthy();
    expect(screen.getByText("Partial", { selector: "span" })).toBeTruthy();
    expect(screen.getByText("Missing", { selector: "span" })).toBeTruthy();
    expect(screen.getByText("Unknown", { selector: "span" })).toBeTruthy();
    expect(screen.queryByText("Available", { selector: "span" })).toBeNull();
    expect(screen.getByText("126 / 180 monitored aired episodes available")).toBeTruthy();
    expect(screen.getByText("Episode data insufficient to classify")).toBeTruthy();
    // Monitored stays a separate indicator alongside availability.
    expect(screen.getAllByText("Monitored").length).toBeGreaterThan(1);
  });
});
