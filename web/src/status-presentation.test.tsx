import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { Series } from "./pages";
import { setToken } from "./api";

// Monitoring vs availability presentation: an unmonitored title must never be headlined
// Missing or Complete. Unmonitored is the primary state; file availability is secondary
// information. Monitored titles keep the availability badge as primary.

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  const routes: Record<string, unknown> = {
    "GET /api/media?type=series": [
      { id: 1, mediaType: "Series", providerId: "1", title: "All Elite Wrestling: Dynamite", year: 2019, overview: null, posterPath: null, monitored: false, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: false, availability: "Missing", availableEpisodes: 0, applicableEpisodes: 368 },
      { id: 2, mediaType: "Series", providerId: "2", title: "Missing Show", year: 2022, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: false, availability: "Missing", availableEpisodes: 0, applicableEpisodes: 40 },
      { id: 3, mediaType: "Series", providerId: "3", title: "Complete Show", year: 2020, overview: null, posterPath: null, monitored: true, qualityProfileId: 1, rootFolder: "/data/tv", hasFile: true, availability: "Complete", availableEpisodes: 180, applicableEpisodes: 180 },
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

function cardFor(title: string): HTMLElement {
  // With no poster image the title also appears in the poster fallback; the card title is
  // the one inside the article element.
  for (const m of screen.getAllByText(title)) {
    const card = m.closest("article");
    if (card) return card as HTMLElement;
  }
  throw new Error(`no poster card for ${title}`);
}

function rowFor(title: string): HTMLElement {
  for (const m of screen.getAllByText(title)) {
    const row = m.closest("tr");
    if (row) return row as HTMLElement;
  }
  throw new Error(`no list row for ${title}`);
}

describe("monitoring vs availability presentation", () => {
  it("shows Unmonitored as the primary state for an unmonitored series, with availability secondary", async () => {
    render(<Series />);
    await screen.findByText("Showing 3 of 3.");
    const aew = within(cardFor("All Elite Wrestling: Dynamite"));
    expect(aew.getByText("Unmonitored", { selector: "span" })).toBeTruthy();
    // No competing Missing badge on this card: availability is secondary text, not a headline.
    expect(aew.queryByText("Missing", { selector: "span" })).toBeNull();
    expect(aew.getByText("Missing · 0 / 368 episodes available")).toBeTruthy();
  });

  it("keeps the availability badge primary for monitored series", async () => {
    render(<Series />);
    await screen.findByText("Showing 3 of 3.");
    const missing = within(cardFor("Missing Show"));
    expect(missing.getByText("Missing", { selector: "span" })).toBeTruthy();
    expect(missing.getByText("Monitored", { selector: "span" })).toBeTruthy();
    const complete = within(cardFor("Complete Show"));
    expect(complete.getByText("Complete", { selector: "span" })).toBeTruthy();
  });

  it("shows the same primary state in List view under a Status column", async () => {
    render(<Series />);
    await screen.findByText("Showing 3 of 3.");
    fireEvent.click(screen.getByRole("button", { name: "List" }));
    expect(screen.getByRole("columnheader", { name: "Status" })).toBeTruthy();
    const inRow = within(rowFor("All Elite Wrestling: Dynamite"));
    expect(inRow.getByText("Unmonitored", { selector: "span" })).toBeTruthy();
    expect(inRow.queryByText("Missing", { selector: "span" })).toBeNull();
    expect(inRow.getByText("Missing · 0 / 368 episodes available")).toBeTruthy();
  });
});
