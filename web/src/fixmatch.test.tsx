import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { Movies, Series } from "./pages";
import { setToken } from "./api";

let requests: { method: string; path: string; body: unknown }[];
let failMatch = false;
let matched = false;
let kind: "Movie" | "Series" = "Movie";

function item() {
  return { id: 1, providerId: matched ? "222" : "111", mediaType: kind,
    title: matched ? "Right Title" : "Wrong Title", year: 2020, monitored: true,
    qualityProfileId: 1, rootFolder: "D:\\Media", hasFile: !matched,
    overview: "Synopsis", posterPath: null, legacyPath: null, legacySource: null };
}

beforeEach(() => {
  requests = []; failMatch = false; matched = false; kind = "Movie";
  setToken("fixture-token");
  vi.stubGlobal("confirm", vi.fn(() => true));
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input), method = init?.method ?? "GET";
    requests.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : null });
    let data: unknown = {};
    let status = 200;
    if (path === "/api/activity/problems") data = [];
    else if (path === "/api/queue") data = { jobs: [], clients: [] };
    else if (path.startsWith("/api/media?")) data = [item()];
    else if (path === "/api/quality-profiles") data = [{ id: 1, name: "HD" }];
    else if (path === "/api/media/1/detail") data = { ...item(), seasons: [], episodes: [], files: [] };
    else if (path.startsWith("/api/metadata/search?")) data = [
      { providerId: "222", title: "Right Title", year: 2020 },
      { providerId: "111", title: "Wrong Title", year: 2020 },
    ];
    else if (path === "/api/media/1/match") {
      if (failMatch) { status = 409; data = { error: "That entry is already in OneArr" }; }
      else { matched = true; data = { title: "Right Title", episodesUpdated: 1, episodesAdded: 0, episodesUnmonitored: 0, filesUnlinked: 1 }; }
    }
    return new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json" } });
  }));
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

async function openFixMatch(series = false) {
  kind = series ? "Series" : "Movie";
  render(series ? <Series /> : <Movies />);
  fireEvent.click(await screen.findByRole("button", { name: "Manage Wrong Title" }));
  const manager = await screen.findByRole("dialog", { name: "Media manager" });
  fireEvent.click(await within(manager).findByRole("button", { name: "Fix match" }));
  fireEvent.click(within(manager).getByRole("button", { name: "Search" }));
  await within(manager).findAllByRole("button", { name: "Use this match" });
  return manager;
}

it.each([false, true])("fixes the match inside the existing manager (series=%s)", async (series) => {
  const manager = await openFixMatch(series);
  fireEvent.click(within(manager).getAllByRole("button", { name: "Use this match" })[0]);
  await within(manager).findByText(/Now matched to Right Title/);
  expect(requests.find(r => r.method === "PUT")).toEqual({ method: "PUT", path: "/api/media/1/match", body: { providerId: "222" } });
  expect(requests.some(r => r.path.includes(`type=${series ? "series" : "movie"}&q=`))).toBe(true);
  expect(within(manager).getByRole("heading", { name: "Right Title (2020)" })).toBeTruthy();
});

it("does not change the match when confirmation is cancelled", async () => {
  const manager = await openFixMatch();
  vi.mocked(confirm).mockReturnValue(false);
  fireEvent.click(within(manager).getAllByRole("button", { name: "Use this match" })[0]);
  expect(requests.some(r => r.method === "PUT")).toBe(false);
});

it("keeps server conflicts visible without closing the manager", async () => {
  const manager = await openFixMatch(); failMatch = true;
  fireEvent.click(within(manager).getAllByRole("button", { name: "Use this match" })[0]);
  await within(manager).findByText("That entry is already in OneArr");
  expect(within(manager).getByRole("heading", { name: "Wrong Title (2020)" })).toBeTruthy();
});

it("refuses to rematch the same provider entry", async () => {
  const manager = await openFixMatch();
  fireEvent.click(within(manager).getAllByRole("button", { name: "Use this match" })[1]);
  expect(within(manager).getByRole("status").textContent).toBe("This is already the matched entry.");
  expect(requests.some(r => r.method === "PUT")).toBe(false);
});
