import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Series } from "./pages";
import { setToken } from "./api";

// Add Series dialog with NO series default, but a movies-looking shared media root stored.
// The dialog must start empty and refuse to add until a folder is typed — it must never
// silently prefill the shared Movies folder for a series.

let posts: { path: string; body?: Record<string, unknown> }[];

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  if (method === "POST" && path === "/api/media") posts.push({ path, body: init?.body ? JSON.parse(String(init.body)) : undefined });
  const routes: Record<string, unknown> = {
    "GET /api/media?type=series": [],
    "GET /api/quality-profiles": [
      { id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true },
    ],
    // No defaultSeriesRoot, but a shared media root that looks like a Movies folder.
    "GET /api/settings": { tmdbConfigured: true, dryRunImport: true, torrentRoot: "/data/torrents", mediaRoot: "/data/movies", automaticGrabbing: false, defaultMovieRoot: "/data/movies", defaultSeriesRoot: null, defaultMovieProfileId: 1, defaultSeriesProfileId: null },
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

describe("add series dialog without a series default", () => {
  it("starts empty, never prefills the shared media root, and requires a typed folder", async () => {
    render(<Series />);
    await screen.findByText("Showing 0 of 0.");
    fireEvent.click(screen.getByRole("button", { name: "Add Series" }));
    await screen.findByRole("dialog", { name: "Add Series" });

    expect((screen.getByLabelText("Root folder") as HTMLInputElement).value).toBe("");
    await screen.findByText(/No saved series default root folder/);

    fireEvent.change(screen.getByLabelText("Search TMDb"), { target: { value: "severance" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await screen.findByText(/Severance/);
    fireEvent.click(screen.getByRole("button", { name: "Select" }));
    await screen.findByText(/Confirm before adding/);
    expect((screen.getByRole("button", { name: "Add Severance" }) as HTMLButtonElement).disabled).toBe(true);
    expect(posts).toHaveLength(0);

    fireEvent.change(screen.getByLabelText("Root folder"), { target: { value: "/data/tv" } });
    fireEvent.click(screen.getByRole("button", { name: "Add Severance" }));
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(posts[0].body!.rootFolder).toBe("/data/tv");
  });
});
