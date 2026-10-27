import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { Series } from "./pages";
import { setToken } from "./api";

// Poster/List switching must preserve the search query, sort, and filter: switching views
// re-renders the same page state, it must not reset the toolbar.

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  const routes: Record<string, unknown> = {
    "GET /api/media?type=series": [
      { id: 1, mediaType: "Series", providerId: "1", title: "Complete Show", year: 2020, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: true, availability: "Complete", availableEpisodes: 180, applicableEpisodes: 180 },
      { id: 2, mediaType: "Series", providerId: "2", title: "Partial Show", year: 2021, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: true, availability: "Partial", availableEpisodes: 126, applicableEpisodes: 180 },
      { id: 3, mediaType: "Series", providerId: "3", title: "Unmonitored Show", year: 2022, overview: null, posterPath: null, monitored: false, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: false, availability: "Missing", availableEpisodes: 0, applicableEpisodes: 40 },
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

describe("poster/list view switching", () => {
  it("preserves search, sort, and filter across view switches", async () => {
    render(<Series />);
    await screen.findByText("Showing 3 of 3.");
    fireEvent.change(screen.getByLabelText(/Search Series/), { target: { value: "show" } });
    fireEvent.change(screen.getByLabelText("Filter"), { target: { value: "monitored" } });
    await screen.findByText("Showing 2 of 3.");
    fireEvent.change(screen.getByLabelText("Sort"), { target: { value: "year" } });

    fireEvent.click(screen.getByRole("button", { name: "List" }));
    expect(screen.getByRole("table")).toBeTruthy();
    expect((screen.getByLabelText(/Search Series/) as HTMLInputElement).value).toBe("show");
    expect((screen.getByLabelText("Filter") as HTMLSelectElement).value).toBe("monitored");
    expect((screen.getByLabelText("Sort") as HTMLSelectElement).value).toBe("year");
    expect(screen.getByText("Showing 2 of 3.")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Posters" }));
    expect(screen.getByText("Showing 2 of 3.")).toBeTruthy();
    expect((screen.getByLabelText(/Search Series/) as HTMLInputElement).value).toBe("show");
    expect((screen.getByLabelText("Filter") as HTMLSelectElement).value).toBe("monitored");
    expect(screen.queryByText("Unmonitored Show")).toBeNull();
  });
});
