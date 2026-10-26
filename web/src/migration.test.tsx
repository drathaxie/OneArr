import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { Migration } from "./pages";
import { setToken } from "./api";

// Monitoring-sync preview gate: Apply stays disabled until a preview for the current source exists,
// changing the source clears it, and loading the migration preview does not clear the API key.

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const body = init?.body ? JSON.parse(String(init.body)) : undefined;
  let payload: unknown = {};
  if (path === "/api/quality-profiles") payload = [{ id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true }];
  else if (path === "/api/migration/preview") payload = { items: [{ type: "series", title: "Show", year: 2020, tmdbId: 123, path: "/data/tv/Show", monitored: true, seasons: [{ seasonNumber: 1, monitored: true }], episodes: [], episodesUnknown: false }], errors: [] };
  else if (path === "/api/migration/monitoring-sync") payload = body?.apply
    ? { applied: true, itemsChecked: 1, skippedNoTmdb: 0, changes: [], unmatchedTitles: [], episodesUnknownTitles: [] }
    : { applied: false, itemsChecked: 1, skippedNoTmdb: 0, changes: [{ tmdbId: 123, title: "Show", type: "series", seriesMonitoredBefore: true, seriesMonitoredAfter: false, monitoredChanged: true, seasonsChanged: 0, episodesChanged: 1, episodesUnknown: false, seasonChanges: [], episodeChanges: [{ seasonNumber: 1, episodeNumber: 2, before: true, after: false }] }], unmatchedTitles: [], episodesUnknownTitles: [] };
  return Promise.resolve(new Response(JSON.stringify(payload), { status: 200, headers: { "Content-Type": "application/json" } }));
}

beforeEach(() => {
  setToken("test-session-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setToken(null);
});

describe("migration monitoring sync gate", () => {
  it("keeps the key after loading preview, gates Apply on a current-source preview, and clears it on source change", async () => {
    render(<Migration />);
    const kind = screen.getByLabelText(/Legacy application/) as HTMLSelectElement;
    fireEvent.change(kind, { target: { value: "sonarr" } });
    fireEvent.change(screen.getByLabelText(/Legacy base URL/), { target: { value: "http://sonarr.test" } });
    fireEvent.change(screen.getByLabelText(/Legacy API key/), { target: { value: "secret-key" } });

    const apply = screen.getByRole("button", { name: "Apply monitoring sync" }) as HTMLButtonElement;
    expect(apply.disabled).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Load preview" }));
    await screen.findByText(/1 readable item/);
    // The key must survive loading the preview, or the sync below would need retyping it.
    expect((screen.getByLabelText(/Legacy API key/) as HTMLInputElement).value).toBe("secret-key");

    fireEvent.click(screen.getByRole("button", { name: "Preview monitoring sync" }));
    await screen.findByText(/Preview only/);
    expect(apply.disabled).toBe(false);
    // The preview lists the exact episode identity, not just a count.
    expect(screen.getByText(/S1E2 on→off/)).toBeTruthy();

    fireEvent.change(screen.getByLabelText(/Legacy base URL/), { target: { value: "http://other.test" } });
    expect(apply.disabled).toBe(true);
  });
});
