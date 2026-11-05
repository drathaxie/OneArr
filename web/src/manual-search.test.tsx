import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ReleasePanel } from "./pages";
import { setToken } from "./api";

// Manual release search tests. The API is replaced at the fetch layer.

interface Call {
  method: string;
  path: string;
  body?: Record<string, unknown>;
}

let calls: Call[];

const CLIENTS = [
  { id: 3, name: "qbit-main", baseUrl: "http://qbit.test:8080", host: "qbit.test", type: "qBittorrent", username: "admin", movieCategory: "onearr-movies", tvCategory: "onearr-tv", enabled: true, hasPassword: true, healthStatus: "Connected", lastSeenAt: null, lastCheckedAt: null, lastError: null },
  { id: 4, name: "qbit-second", baseUrl: "http://qbit2.test:8080", host: "qbit2.test", type: "qBittorrent", username: "admin", movieCategory: "onearr-movies", tvCategory: "onearr-tv", enabled: true, hasPassword: true, healthStatus: "Connected", lastSeenAt: null, lastCheckedAt: null, lastError: null },
];

const THREE_DAYS_AGO = new Date(Date.now() - 3 * 86_400_000).toISOString();
const ONE_HOUR_AGO = new Date(Date.now() - 3_600_000).toISOString();

const SEARCH_BODY = {
  indexers: [
    { id: 7, name: "Zulu", status: "results", resultCount: 1, message: null },
    { id: 8, name: "Alpha", status: "no-results", resultCount: 0, message: null },
    { id: 9, name: "Broken", status: "failed", resultCount: 0, message: "Network error" },
    { id: 10, name: "Off", status: "skipped", resultCount: 0, message: "Disabled" },
  ],
  results: [
    { title: "Movie.2021.1080p.WEB", link: "magnet:?xt=urn:btih:AAAA", infoHash: "aaaa", size: 2_147_483_648, seeders: 5, indexer: "Zulu", resolution: 1080, source: "WEB", accepted: true, score: 80, reasons: [], publishDateUtc: THREE_DAYS_AGO },
    { title: "Movie.2021.720p.WEB", link: "magnet:?xt=urn:btih:BBBB", infoHash: "bbbb", size: 1_073_741_824, seeders: 50, indexer: "Alpha", resolution: 720, source: "WEB", accepted: false, score: 40, reasons: ["Below minimum resolution"], publishDateUtc: ONE_HOUR_AGO },
  ],
};

function fakeFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const path = String(input);
  const method = init?.method ?? "GET";
  const call: Call = { method, path, body: init?.body ? JSON.parse(String(init.body)) : undefined };
  calls.push(call);
  if (method === "GET" && path === "/api/download-clients") {
    return Promise.resolve(new Response(JSON.stringify(CLIENTS), { status: 200, headers: { "Content-Type": "application/json" } }));
  }
  if (method === "POST" && path === "/api/search") {
    return Promise.resolve(new Response(JSON.stringify(SEARCH_BODY), { status: 200, headers: { "Content-Type": "application/json" } }));
  }
  if (method === "POST" && path === "/api/grab") {
    return Promise.resolve(new Response(JSON.stringify({ id: 1 }), { status: 201, headers: { "Content-Type": "application/json" } }));
  }
  return Promise.resolve(new Response(JSON.stringify({ error: `unexpected ${method} ${path}` }), { status: 404, headers: { "Content-Type": "application/json" } }));
}

beforeEach(() => {
  calls = [];
  setToken("test-token");
  vi.stubGlobal("fetch", fakeFetch);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function openPanel() {
  render(<ReleasePanel mediaId={1} episodeId={null} onClose={() => {}} />);
  fireEvent.click(screen.getByRole("button", { name: "Search indexers" }));
  await screen.findByText("Movie.2021.1080p.WEB");
}

describe("Manual release search", () => {
  it("shows per-indexer outcomes and release age", async () => {
    await openPanel();
    expect(screen.getByText(/Zulu: 1 release/)).toBeTruthy();
    expect(screen.getByText(/Alpha: no releases/)).toBeTruthy();
    expect(screen.getByText(/Broken: failed — Network error/)).toBeTruthy();
    expect(screen.getByText(/Off: skipped — Disabled/)).toBeTruthy();
    expect(screen.getByText("3d")).toBeTruthy();
    expect(screen.getByText("1h")).toBeTruthy();
  });

  it("filters by indexer and decision and sorts by seeders", async () => {
    await openPanel();
    fireEvent.change(screen.getByLabelText("Filter by indexer"), { target: { value: "Alpha" } });
    expect(screen.queryByText("Movie.2021.1080p.WEB")).toBeNull();
    expect(screen.getByText("Movie.2021.720p.WEB")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Filter by indexer"), { target: { value: "all" } });
    fireEvent.change(screen.getByLabelText("Filter by decision"), { target: { value: "accepted" } });
    expect(screen.queryByText("Movie.2021.720p.WEB")).toBeNull();
    expect(screen.getByText("Movie.2021.1080p.WEB")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Filter by decision"), { target: { value: "all" } });
    fireEvent.change(screen.getByLabelText("Sort releases"), { target: { value: "seeders" } });
    const text = document.body.textContent ?? "";
    expect(text.indexOf("Movie.2021.720p.WEB")).toBeLessThan(text.indexOf("Movie.2021.1080p.WEB"));
  });

  it("shows a successful qBittorrent grab in success styling while explaining import is pending", async () => {
    await openPanel();
    fireEvent.click(screen.getAllByRole("button", { name: "Grab" })[0]);
    const status = await screen.findByRole("status");
    expect(status.className).toContain("ok");
    expect(status.textContent).toContain("Sent to qBittorrent:");
    expect(status.textContent).toContain("only after import completes");
    expect(status.textContent).toContain("Activity → Queue");
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("grabs to the selected client, defaulting to the first enabled one", async () => {
    await openPanel();
    fireEvent.click(screen.getAllByRole("button", { name: "Grab" })[0]);
    await waitFor(() => expect(calls.some((c) => c.method === "POST" && c.path === "/api/grab")).toBe(true));
    const first = calls.find((c) => c.method === "POST" && c.path === "/api/grab")!;
    expect(first.body!.downloadClientId).toBe(3);
    fireEvent.change(screen.getByLabelText("Download client"), { target: { value: "4" } });
    fireEvent.click(screen.getAllByRole("button", { name: "Grab" })[0]);
    await waitFor(() => expect(calls.filter((c) => c.method === "POST" && c.path === "/api/grab").length).toBe(2));
    const second = calls.filter((c) => c.method === "POST" && c.path === "/api/grab")[1]!;
    expect(second.body!.downloadClientId).toBe(4);
  });
});
