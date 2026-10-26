import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Settings } from "./pages";
import { setToken } from "./api";

// Settings navigation tests: overview, sidebar categories, active highlighting, expand/collapse,
// the Advanced toggle, per-type defaults, and the UI view preference.

let calls: { method: string; path: string; body?: Record<string, unknown> }[];

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  calls.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : undefined });
  const routes: Record<string, unknown> = {
    "GET /api/settings": { tmdbConfigured: false, dryRunImport: true, torrentRoot: "/data/torrents", mediaRoot: "/data/media", automaticGrabbing: false, defaultMovieRoot: "/data/movies", defaultSeriesRoot: "/data/tv", defaultMovieProfileId: 1, defaultSeriesProfileId: 2 },
    "GET /api/quality-profiles": [
      { id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true },
      { id: 2, name: "HD-2160p", minResolution: 1080, cutoffResolution: 2160, allowWebSources: true },
    ],
    "GET /api/indexers": [],
    "GET /api/download-clients": [],
    "GET /api/library/status": { windows: false, automation: { allowed: false, reason: "library not reconciled" }, roots: [], mappings: [] },
    "GET /api/library/mapping-suggestions": { suggestions: [], entriesWithoutLegacyPath: 0, rootFolders: [] },
    "GET /api/auth/status": { configured: true, authenticated: true },
    "PUT /api/settings": null,
  };
  const body = routes[`${method} ${path}`];
  if (body === undefined) return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${method} ${path}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
  return Promise.resolve(new Response(method === "PUT" ? null : JSON.stringify(body), { status: method === "PUT" ? 204 : 200, headers: { "Content-Type": "application/json" } }));
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

describe("settings navigation", () => {
  it("shows an overview of every category and opens one from its card", async () => {
    render(<Settings />);
    await screen.findByRole("heading", { name: "Settings" });
    for (const label of ["Media Management", "Profiles", "Indexers", "Download Clients", "Metadata", "Automation", "Migration", "General", "UI"]) {
      expect(screen.getByRole("heading", { name: label })).toBeTruthy();
    }
    fireEvent.click(screen.getByRole("button", { name: "Open Profiles" }));
    await screen.findByRole("heading", { name: "Quality profiles" });
  });

  it("highlights the active sidebar section and expands its subsections", async () => {
    render(<Settings />);
    await screen.findByRole("heading", { name: "Settings" });
    const profilesButton = screen.getByRole("button", { name: "Profiles" });
    fireEvent.click(profilesButton);
    await screen.findByRole("heading", { name: "Quality profiles" });
    expect(profilesButton.className).toContain("active");
    expect(screen.getByRole("button", { name: "Default profiles" })).toBeTruthy();
  });

  it("hides advanced items when advanced is off and reveals them when it is on", async () => {
    render(<Settings />);
    await screen.findByRole("heading", { name: "Settings" });
    fireEvent.click(screen.getByRole("button", { name: "Hide Advanced" }));
    expect(screen.getAllByText(/Advanced items .* are hidden/).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Show Advanced" }));
    await waitFor(() => expect(screen.queryAllByText(/Advanced items .* are hidden/)).toHaveLength(0));
  });

  it("saves movie and series default roots independently", async () => {
    render(<Settings />);
    await screen.findByRole("heading", { name: "Settings" });
    fireEvent.click(screen.getByRole("button", { name: "Media Management" }));
    await screen.findByRole("heading", { name: "Default root folders" });
    fireEvent.change(screen.getByLabelText("Default movie root folder"), { target: { value: "/mnt/movies" } });
    fireEvent.change(screen.getByLabelText("Default series root folder"), { target: { value: "/mnt/tv" } });
    fireEvent.click(screen.getByRole("button", { name: "Save defaults" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.path === "/api/settings")).toBe(true));
    const put = calls.find((c) => c.method === "PUT")!;
    expect(put.body).toEqual({ defaultMovieRoot: "/mnt/movies", defaultSeriesRoot: "/mnt/tv" });
  });

  it("saves movie and series default profiles independently", async () => {
    render(<Settings />);
    await screen.findByRole("heading", { name: "Settings" });
    fireEvent.click(screen.getByRole("button", { name: "Profiles" }));
    await screen.findByRole("heading", { name: "Default profiles" });
    fireEvent.change(screen.getByLabelText("Default movie profile"), { target: { value: "2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save default profiles" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.path === "/api/settings")).toBe(true));
    const put = calls.find((c) => c.method === "PUT")!;
    expect(put.body).toEqual({ defaultMovieProfileId: 2, defaultSeriesProfileId: 2 });
  });

  it("persists the Posters/List preference in the UI section", async () => {
    render(<Settings />);
    await screen.findByRole("heading", { name: "Settings" });
    fireEvent.click(screen.getByRole("button", { name: "UI" }));
    await screen.findByRole("heading", { name: "Default view" });
    fireEvent.click(screen.getByLabelText("List"));
    expect(localStorage.getItem("onearr.view")).toBe("list");
    fireEvent.click(screen.getByLabelText("Posters"));
    expect(localStorage.getItem("onearr.view")).toBe("grid");
  });
});
