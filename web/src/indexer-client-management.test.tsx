import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { Settings } from "./pages";
import { setToken } from "./api";

// Indexer and download-client management tests. The API is replaced at the fetch layer.

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

const INDEXERS = [
  { id: 7, name: "Zulu", baseUrl: "http://zulu.test/", host: "zulu.test", protocol: "Torznab", enabled: true, moviesEnabled: true, tvEnabled: true, priority: 0, hasKey: true, healthStatus: "Healthy", lastTestedAt: "2026-10-27T10:00:00Z", lastError: null },
  { id: 8, name: "Alpha", baseUrl: "http://alpha.test/", host: "alpha.test", protocol: "Torznab", enabled: false, moviesEnabled: true, tvEnabled: false, priority: 5, hasKey: false, healthStatus: "Disabled", lastTestedAt: null, lastError: null },
];

const CLIENTS = [
  { id: 3, name: "qbit-main", baseUrl: "http://qbit.test:8080", host: "qbit.test", type: "qBittorrent", username: "admin", movieCategory: "onearr-movies", tvCategory: "onearr-tv", enabled: true, hasPassword: true, healthStatus: "Connected", lastSeenAt: "2026-10-27T10:00:00Z", lastCheckedAt: "2026-10-27T10:00:00Z", lastError: null },
];

function defaultRoutes(): Record<string, Handler> {
  return {
    "GET /api/settings": () => ({
      body: { tmdbConfigured: true, dryRunImport: true, torrentRoot: "/data/torrents", mediaRoot: "/data/media", automaticGrabbing: false, defaultMovieRoot: "/data/movies", defaultSeriesRoot: "/data/tv", defaultMovieProfileId: 1, defaultSeriesProfileId: 1 },
    }),
    "GET /api/auth/status": () => ({ body: { configured: true, authenticated: true } }),
    "GET /api/quality-profiles": () => ({ body: [{ id: 1, name: "HD-1080p", minResolution: 720, cutoffResolution: 1080, allowWebSources: true }] }),
    "GET /api/indexers": () => ({ body: INDEXERS }),
    "GET /api/download-clients": () => ({ body: CLIENTS }),
    "GET /api/library/status": () => ({ body: { windows: false, automation: { allowed: true, reason: "ok" }, roots: [], mappings: [] } }),
    "POST /api/connections/test": () => ({ body: { ok: true, service: "torznab", detail: "Indexer answered caps with 3 categories.", categories: [{ id: 2000, name: "Movies" }], searchAvailable: true, tvSearchAvailable: true, movieSearchAvailable: true } }),
    "POST /api/connections/test-all": () => ({ body: { results: [{ kind: "indexer", id: 7, name: "Zulu", ok: true, detail: "ok" }], tested: 1, passed: 1 } }),
  };
}

async function renderSettings() {
  render(<Settings />);
  await screen.findByRole("heading", { name: "Settings" });
}

async function openSection(label: string, waitText: string) {
  fireEvent.click(screen.getByRole("button", { name: label }));
  await screen.findByRole("heading", { name: waitText });
}

beforeEach(() => {
  calls = [];
  routes = defaultRoutes();
  setToken("test-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Indexer management", () => {
  it("lists indexers with health and filters by name", async () => {
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    expect(await screen.findByText("Zulu")).toBeTruthy();
    expect(screen.getByText("Healthy", { selector: "span" })).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Filter indexers"), { target: { value: "alp" } });
    expect(screen.queryByText("Zulu")).toBeNull();
    expect(screen.getByText("Alpha")).toBeTruthy();
  });

  it("sorts by name when asked", async () => {
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    await screen.findByText("Zulu");
    fireEvent.change(screen.getByLabelText("Sort indexers"), { target: { value: "name" } });
    const text = document.body.textContent ?? "";
    expect(text.indexOf("Alpha")).toBeLessThan(text.indexOf("Zulu"));
  });

  it("tests a row by saved id and shows the detail", async () => {
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    await screen.findByText("Zulu");
    fireEvent.click(screen.getAllByRole("button", { name: "Test" })[0]);
    expect(await screen.findByText(/answered caps with 3 categories/)).toBeTruthy();
    const test = calls.find((c) => c.method === "POST" && c.path === "/api/connections/test")!;
    expect(test.body).toEqual({ service: "torznab", indexerId: 7 });
  });

  it("tests unsaved form values without saving", async () => {
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    fireEvent.click(screen.getByRole("button", { name: "Add indexer" }));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "draft" } });
    fireEvent.change(screen.getByLabelText("URL"), { target: { value: "http://draft.test/" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "draft-key" } });
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Test" }));
    expect(await screen.findByText(/answered caps with 3 categories/)).toBeTruthy();
    const test = calls.find((c) => c.method === "POST" && c.path === "/api/connections/test")!;
    expect(test.body).toEqual({ service: "torznab", baseUrl: "http://draft.test/", apiKey: "draft-key" });
    expect(calls.some((c) => c.method === "POST" && c.path === "/api/indexers")).toBe(false);
  });

  it("tests an edit dialog with the saved id so a blank key reuses the stored key", async () => {
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    await screen.findByText("Zulu");
    fireEvent.click(screen.getAllByRole("button", { name: "Edit" })[0]);
    fireEvent.change(screen.getByLabelText("URL"), { target: { value: "http://zulu2.test/" } });
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Test" }));
    expect(await screen.findByText(/answered caps with 3 categories/)).toBeTruthy();
    const test = calls.find((c) => c.method === "POST" && c.path === "/api/connections/test")!;
    expect(test.body).toEqual({ service: "torznab", indexerId: 7, baseUrl: "http://zulu2.test/", apiKey: "" });
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  it("edits without clearing the stored key", async () => {
    routes["PUT /api/indexers/7"] = () => ({ status: 200, body: {} });
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    await screen.findByText("Zulu");
    fireEvent.click(screen.getAllByRole("button", { name: "Edit" })[0]);
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Zulu-renamed" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.path === "/api/indexers/7")).toBe(true));
    const put = calls.find((c) => c.method === "PUT" && c.path === "/api/indexers/7")!;
    expect(put.body!.name).toBe("Zulu-renamed");
    expect(put.body!.apiKey).toBe("");
  });

  it("deletes only after confirmation", async () => {
    routes["DELETE /api/indexers/7"] = () => ({ status: 204 });
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    await screen.findByText("Zulu");
    fireEvent.click(screen.getAllByRole("button", { name: "Delete" })[0]);
    expect(calls.some((c) => c.method === "DELETE")).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Confirm delete" }));
    await waitFor(() => expect(calls.some((c) => c.method === "DELETE" && c.path === "/api/indexers/7")).toBe(true));
  });

  it("tests all enabled indexers and clients in one call", async () => {
    await renderSettings();
    await openSection("Indexers", "Indexers (Torznab / Newznab)");
    await screen.findByText("Zulu");
    fireEvent.click(screen.getByRole("button", { name: "Test all" }));
    expect(await screen.findByText(/Tested 1, passed 1/)).toBeTruthy();
    expect(calls.some((c) => c.method === "POST" && c.path === "/api/connections/test-all")).toBe(true);
  });
});

describe("Download client management", () => {
  it("lists clients with health and tests a row by saved id", async () => {
    await renderSettings();
    await openSection("Download Clients", "Download clients (qBittorrent)");
    expect(await screen.findByText("qbit-main")).toBeTruthy();
    expect(screen.getByText("Connected", { selector: "span" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Test" }));
    const test = calls.find((c) => c.method === "POST" && c.path === "/api/connections/test")!;
    expect(test.body).toEqual({ service: "qbittorrent", clientId: 3 });
  });

  it("tests edit-dialog values with the saved id and edits without clearing the password", async () => {
    routes["PUT /api/download-clients/3"] = () => ({ status: 200, body: {} });
    await renderSettings();
    await openSection("Download Clients", "Download clients (qBittorrent)");
    await screen.findByText("qbit-main");
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.change(screen.getByLabelText("URL"), { target: { value: "http://qbit2.test:8080" } });
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Test" }));
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/api/connections/test")).toBe(true));
    const test = calls.find((c) => c.method === "POST" && c.path === "/api/connections/test")!;
    expect(test.body!.service).toBe("qbittorrent");
    expect(test.body!.clientId).toBe(3);
    expect(test.body!.baseUrl).toBe("http://qbit2.test:8080");
    expect(test.body!.password).toBe("");
    expect(calls.some((c) => c.method === "POST" && c.path === "/api/download-clients")).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.some((c) => c.method === "PUT" && c.path === "/api/download-clients/3")).toBe(true));
    const put = calls.find((c) => c.method === "PUT" && c.path === "/api/download-clients/3")!;
    expect(put.body!.password).toBe("");
  });
});
