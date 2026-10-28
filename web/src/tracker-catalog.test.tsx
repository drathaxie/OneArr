import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { TrackerCatalog } from "./tracker-catalog";

const definition = `
id: nyaasi
name: Nyaa.si
description: Public anime torrent site
language: en-US
type: public
links:
  - https://nyaa.si/
caps:
  categorymappings:
    - {id: 1_2, cat: TV/Anime, desc: "Anime"}
settings:
  - name: prefer_magnet_links
    type: checkbox
    label: Prefer magnet links
    default: true
`;

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (String(url).endsWith("/definitions/v11/nyaasi.yml"))
      return new Response(definition, { status: 200 });
    return new Response("not found", { status: 404 });
  }));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Tracker directory", () => {
  it("works offline from the bundled complete definitions manifest", () => {
    render(<TrackerCatalog onClose={() => {}} onGeneric={() => {}} />);
    expect(screen.getByText(/579 definitions/)).toBeTruthy();
    expect(screen.getByText(/Bundled catalog/)).toBeTruthy();
  });

  it("filters the entire offline directory by privacy, language and media", () => {
    render(<TrackerCatalog onClose={() => {}} onGeneric={() => {}} />);
    fireEvent.change(screen.getByLabelText("Tracker privacy"), { target: { value: "private" } });
    fireEvent.change(screen.getByLabelText("Tracker language"), { target: { value: "en-US" } });
    fireEvent.change(screen.getByLabelText("Tracker categories"), { target: { value: "tv" } });
    fireEvent.change(screen.getByLabelText("Search tracker catalog"), { target: { value: "torrentleech" } });
    expect(screen.getByText("TorrentLeech")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Tracker privacy"), { target: { value: "public" } });
    expect(screen.queryByText("TorrentLeech")).toBeNull();
  });

  it("finds Nyaa, displays setup requirements and hands off its native RSS address", async () => {
    const onGeneric = vi.fn();
    render(<TrackerCatalog onClose={() => {}} onGeneric={onGeneric} />);
    fireEvent.change(screen.getByLabelText("Search tracker catalog"), { target: { value: "nyaasi" } });
    expect(screen.queryByText("Torrentleech")).toBeNull();
    fireEvent.click(screen.getByText("Nyaa.si").closest("button")!);
    expect(await screen.findByRole("heading", { name: "Nyaa.si" })).toBeTruthy();
    expect(screen.getByText(/Prefer magnet links/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Add Nyaa (native RSS)" }));
    expect(onGeneric).toHaveBeenCalledWith("Nyaa.si", "https://nyaa.si/");
  });

  it("never claims private catalog entries can log in without a native adapter", async () => {
    const onGeneric = vi.fn();
    const privateDefinition = `
id: torrentleech
name: TorrentLeech
description: Private tracker
language: en-US
type: private
links:
  - https://www.torrentleech.org/
settings:
  - name: username
    type: text
    label: Username
  - name: password
    type: password
    label: Password
`;
    vi.stubGlobal("fetch", vi.fn(async () => new Response(privateDefinition, { status: 200 })));
    render(<TrackerCatalog onClose={() => {}} onGeneric={onGeneric} />);
    fireEvent.change(screen.getByLabelText("Search tracker catalog"), { target: { value: "torrentleech" } });
    fireEvent.click(screen.getByText("TorrentLeech").closest("button")!);
    await waitFor(() => expect(screen.getByText(/Tracker-specific credentials are shown for reference only/)).toBeTruthy());
    expect(screen.getByText(/Username/)).toBeTruthy();
    expect(screen.getByText(/Password/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Save login/i })).toBeNull();
  });
});
