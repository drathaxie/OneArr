import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { Settings } from "./pages";
import { setToken } from "./api";

// Settings screen tests. The API is replaced at the fetch layer so the real request/response handling is exercised.

interface Call {
  method: string;
  path: string;
  body?: Record<string, unknown>;
}

type Handler = (call: Call) => { status?: number; body?: unknown };

let calls: Call[];
let routes: Record<string, Handler>;

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  const call: Call = { method, path, body: init?.body ? JSON.parse(String(init.body)) : undefined };
  calls.push(call);
  const key = `${method} ${path}`;
  const handler = routes[key] ?? (() => ({ status: 404, body: { error: `unexpected request ${key}` } }));
  const { status = 200, body } = handler(call);
  return Promise.resolve(new Response(status === 204 ? null : JSON.stringify(body ?? null), {
    status,
    headers: { "Content-Type": "application/json" },
  }));
}

function defaultRoutes(): Record<string, Handler> {
  return {
    "GET /api/settings": () => ({
      body: { tmdbConfigured: true, dryRunImport: true, torrentRoot: "/data/torrents", mediaRoot: "/data/media", automaticGrabbing: false, defaultMovieRoot: "/data/movies", defaultSeriesRoot: "/data/tv", defaultMovieProfileId: 1, defaultSeriesProfileId: 1 },
    }),
    "GET /api/auth/status": () => ({ body: { configured: true, authenticated: true } }),
    "GET /api/quality-profiles": () => ({ body: [{ id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true }] }),
    "GET /api/indexers": () => ({ body: [] }),
    "GET /api/download-clients": () => ({ body: [] }),
    "GET /api/library/status": () => ({ body: { windows: false, automation: { allowed: true, reason: "All library folders are reachable and scanned" }, roots: [], mappings: [] } }),
    "PUT /api/settings": () => ({ status: 204 }),
    "POST /api/connections/test": () => ({ body: { ok: true, service: "tmdb", detail: "TMDb answered a test search with 3 result(s)." } }),
  };
}

async function renderSettings() {
  render(<Settings />);
  await screen.findByRole("heading", { name: "Settings" });
}

async function openSection(label: string, waitText?: string) {
  fireEvent.click(screen.getByRole("button", { name: label }));
  if (waitText) await screen.findByRole("heading", { name: waitText });
}

function putCalls() {
  return calls.filter((c) => c.method === "PUT" && c.path === "/api/settings");
}

beforeEach(() => {
  calls = [];
  routes = defaultRoutes();
  setToken("test-session-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  setToken(null);
});

describe("General settings", () => {
  it("shows the TMDb key as configured without ever filling the field with it", async () => {
    await renderSettings();
    await openSection("Metadata", "TMDb");
    expect(screen.getByText("configured")).toBeTruthy();
    expect((screen.getByLabelText(/TMDb API key/) as HTMLInputElement).value).toBe("");
  });

  it("saves paths and import mode without sending a TMDb key when the field is blank", async () => {
    await renderSettings();
    await openSection("Media Management", "Default root folders");
    fireEvent.change(screen.getByLabelText(/Torrent download root/), { target: { value: "/mnt/torrents" } });
    fireEvent.click(screen.getByRole("button", { name: "Save import paths" }));

    await waitFor(() => expect(putCalls()).toHaveLength(1));
    const body = putCalls()[0].body!;
    expect(body).toEqual({ torrentRoot: "/mnt/torrents", mediaRoot: "/data/media", dryRunImport: true });
    expect(JSON.stringify(body)).not.toContain("tmdbApiKey");
  });

  it("sends a newly typed TMDb key", async () => {
    await renderSettings();
    await openSection("Metadata", "TMDb");
    fireEvent.change(screen.getByLabelText(/TMDb API key/), { target: { value: "tmdb-test-key-123" } });
    fireEvent.click(screen.getByRole("button", { name: "Save TMDb key" }));

    await waitFor(() => expect(putCalls()).toHaveLength(1));
    expect(putCalls()[0].body!.tmdbApiKey).toBe("tmdb-test-key-123");
  });

  it("submits the dry-run toggle as turned off", async () => {
    await renderSettings();
    await openSection("Media Management", "Default root folders");
    fireEvent.click(screen.getByLabelText(/Imports are dry-run only/));
    fireEvent.click(screen.getByRole("button", { name: "Save import paths" }));

    await waitFor(() => expect(putCalls()).toHaveLength(1));
    expect(putCalls()[0].body!.dryRunImport).toBe(false);
  });

  it("submits the automatic-grabbing toggle as turned on", async () => {
    await renderSettings();
    await openSection("Automation", "Automatic grabbing");
    fireEvent.click(screen.getByLabelText(/Automatic grabbing/));
    fireEvent.click(screen.getByRole("button", { name: "Save automation" }));

    await waitFor(() => expect(putCalls()).toHaveLength(1));
    expect(putCalls()[0].body!.automaticGrabbing).toBe(true);
  });

  it("runs a TMDb connection test and shows its result", async () => {
    await renderSettings();
    await openSection("Metadata", "TMDb");
    fireEvent.click(screen.getByRole("button", { name: "Test TMDb" }));

    expect(await screen.findByText(/TMDb answered a test search/)).toBeTruthy();
    expect(calls.some((c) => c.method === "POST" && c.path === "/api/connections/test")).toBe(true);
  });

  it("shows the server's validation message when a path is refused", async () => {
    routes["PUT /api/settings"] = () => ({ status: 400, body: { error: "Path must be absolute and must not contain '..'" } });
    await renderSettings();
    await openSection("Media Management", "Default root folders");
    fireEvent.change(screen.getByLabelText(/Media root/), { target: { value: "relative/media" } });
    fireEvent.click(screen.getByRole("button", { name: "Save import paths" }));

    expect(await screen.findByText(/Path must be absolute/)).toBeTruthy();
  });
});

describe("Quality profile settings", () => {
  it("creates a profile with the chosen resolutions", async () => {
    routes["POST /api/quality-profiles"] = () => ({ status: 201, body: { id: 2 } });
    await renderSettings();
    await openSection("Profiles", "Quality profiles");
    fireEvent.change(screen.getByLabelText("Profile name"), { target: { value: "Test-4K" } });
    fireEvent.change(screen.getByLabelText("Minimum resolution"), { target: { value: "1080" } });
    fireEvent.change(screen.getByLabelText("Cutoff resolution"), { target: { value: "2160" } });
    fireEvent.click(screen.getByRole("button", { name: "Add profile" }));

    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/api/quality-profiles")).toBe(true));
    const post = calls.find((c) => c.method === "POST" && c.path === "/api/quality-profiles")!;
    expect(post.body).toEqual({ name: "Test-4K", minResolution: 1080, cutoffResolution: 2160, allowWebSources: true });
  });

  it("shows an error when the cutoff is below the minimum", async () => {
    routes["POST /api/quality-profiles"] = () => ({ status: 400, body: { error: "Resolutions must be 480, 720, 1080 or 2160, with cutoff >= minimum" } });
    await renderSettings();
    await openSection("Profiles", "Quality profiles");
    fireEvent.change(screen.getByLabelText("Profile name"), { target: { value: "Bad" } });
    fireEvent.change(screen.getByLabelText("Minimum resolution"), { target: { value: "2160" } });
    fireEvent.change(screen.getByLabelText("Cutoff resolution"), { target: { value: "720" } });
    fireEvent.click(screen.getByRole("button", { name: "Add profile" }));

    expect(await screen.findByText(/Cutoff cannot be lower than the minimum resolution/)).toBeTruthy();
  });
});

describe("Quality profile management", () => {
  it("edits a profile without changing its identity or deleting media", async () => {
    routes["PUT /api/quality-profiles/1"] = () => ({
      body: { id: 1, name: "HD edited", minResolution: 1080, cutoffResolution: 2160, allowWebSources: false },
    });
    await renderSettings();
    await openSection("Profiles", "Quality profiles");
    fireEvent.click(screen.getByRole("button", { name: "Edit profile HD-1080p" }));
    fireEvent.change(screen.getByLabelText("Edit profile name"), { target: { value: "HD edited" } });
    const edit = screen.getByRole("form", { name: "Edit HD-1080p" });
    fireEvent.change(edit.querySelector('select[aria-label="Minimum resolution"]')!, { target: { value: "1080" } });
    fireEvent.change(edit.querySelector('select[aria-label="Cutoff resolution"]')!, { target: { value: "2160" } });
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(calls.some(x => x.path === "/api/quality-profiles/1" && x.method === "PUT")).toBe(true));
    const put = calls.find(x => x.path === "/api/quality-profiles/1" && x.method === "PUT")!;
    expect(put.body).toEqual({ name: "HD edited", minResolution: 1080, cutoffResolution: 2160, allowWebSources: true });
  });

  it("asks for explicit delete confirmation and shows an in-use conflict", async () => {
    routes["DELETE /api/quality-profiles/1"] = () => ({ status: 409, body: { error: "Profile is assigned to existing movies." } });
    await renderSettings();
    await openSection("Profiles", "Quality profiles");
    fireEvent.click(screen.getByRole("button", { name: "Delete profile HD-1080p" }));
    expect(calls.filter(x => x.method === "DELETE")).toHaveLength(0);
    fireEvent.click(screen.getByRole("button", { name: "Confirm delete" }));
    await waitFor(() => expect(calls.some(x => x.method === "DELETE" && x.path === "/api/quality-profiles/1")).toBe(true));
    expect(await screen.findByText(/assigned to existing movies/)).toBeTruthy();
  });
});

describe("Indexer settings", () => {
  it("sends the API key on create but never renders it back", async () => {
    const secret = "SUPER-SECRET-INDEXER-KEY";
    routes["POST /api/indexers"] = () => ({ status: 201, body: {} });
    routes["GET /api/indexers"] = () => ({
      body: calls.some((c) => c.method === "POST" && c.path === "/api/indexers")
        ? [{ id: 7, name: "fixture", baseUrl: "http://127.0.0.1:9002/", host: "127.0.0.1", protocol: "Torznab", enabled: true, moviesEnabled: true, tvEnabled: true, priority: 0, hasKey: true, healthStatus: "Not tested", lastTestedAt: null, lastError: null }]
        : [],
    });
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / RSS)");
    fireEvent.click(screen.getByRole("button", { name: "Add indexer" }));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "fixture" } });
    fireEvent.change(screen.getByLabelText("URL"), { target: { value: "http://127.0.0.1:9002/" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: secret } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("fixture")).toBeTruthy();
    const post = calls.find((c) => c.method === "POST" && c.path === "/api/indexers")!;
    expect(post.body!.apiKey).toBe(secret);
    expect(document.body.textContent).not.toContain(secret);
  });
});
