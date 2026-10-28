import { useEffect, useMemo, useState } from "react";
import bundled from "./tracker-catalog.json";

type CatalogEntry = { path: string; name: string; privacy: string; language: string; movies: boolean; tv: boolean };
const bundledEntries: CatalogEntry[] = bundled.entries;
const metadata = new Map(bundledEntries.map(item => [item.path, item]));
type TrackerField = { name: string; label: string; type: string; defaultValue?: string };
type TrackerInfo = {
  name: string; id: string; description: string; language: string; privacy: string;
  links: string[]; fields: TrackerField[]; supportsMovies: boolean; supportsTv: boolean;
};
const UPSTREAM_TREE = "https://api.github.com/repos/Prowlarr/Indexers/git/trees/master?recursive=1";
const UPSTREAM_RAW = "https://raw.githubusercontent.com/Prowlarr/Indexers/master/";
const validPath = /^definitions\/v\d+\/[a-zA-Z0-9-]+\.yml$/;

function clean(value: string): string {
  const trim = value.trim();
  if ((trim.startsWith('"') && trim.endsWith('"')) || (trim.startsWith("'") && trim.endsWith("'")))
    return trim.slice(1, -1);
  return trim;
}

function parseDefinition(text: string, fallback: string): TrackerInfo {
  // Parse just display metadata and settings labels. The YAML is NEVER
  // interpreted or executed in the browser and no secrets are sent to GitHub.
  const lines = text.split(/\r?\n/);
  const top = (key: string) => clean(lines.find(line => line.startsWith(key + ":"))?.slice(key.length + 1) ?? "");
  const info: TrackerInfo = {
    id: top("id") || fallback, name: top("name") || fallback,
    description: top("description"), language: top("language"),
    privacy: top("type") || "unknown", links: [], fields: [],
    supportsMovies: false, supportsTv: false,
  };
  let section = "";
  let field: TrackerField | null = null;
  for (const line of lines) {
    if (/^[a-zA-Z][\w-]*:/.test(line)) { section = line.split(":", 1)[0]; field = null; }
    if (section === "links" && /^  - https?:\/\//.test(line)) info.links.push(line.slice(4).trim().split(/\s+#/)[0]);
    if (section === "caps") {
      if (/cat: Movies(?:[\/,} ]|$)/i.test(line)) info.supportsMovies = true;
      if (/cat: TV(?:[\/,} ]|$)/i.test(line)) info.supportsTv = true;
    }
    if (section === "settings") {
      const next = line.match(/^  - name:\s*(.*)$/);
      if (next) {
        field = { name: clean(next[1]), label: clean(next[1]), type: "text" };
        info.fields.push(field);
      } else if (field) {
        const label = line.match(/^    label:\s*(.*)$/);
        const kind = line.match(/^    type:\s*(.*)$/);
        const def = line.match(/^    default:\s*(.*)$/);
        if (label) field.label = clean(label[1]);
        if (kind) field.type = clean(kind[1]);
        if (def) field.defaultValue = clean(def[1]);
      }
    }
  }
  info.fields = info.fields.filter(f => !f.type.startsWith("info") && f.type !== "hidden");
  return info;
}

function titleFromPath(path: string): string {
  const base = path.split("/").pop()?.replace(/\.yml$/, "") ?? path;
  return base.split("-").map(word => word === "api" ? "API" : word.charAt(0).toUpperCase() + word.slice(1)).join(" ");
}

export function TrackerCatalog({ onClose, onGeneric, onNative }: { onClose: () => void; onGeneric: (name: string, url?: string) => void; onNative?: (name: string, url: string, trackerId: string) => void }) {
  const [paths, setPaths] = useState<string[]>(bundled.definitionPaths);
  const [filter, setFilter] = useState("");
  const [privacyFilter, setPrivacyFilter] = useState("all");
  const [languageFilter, setLanguageFilter] = useState("all");
  const [mediaFilter, setMediaFilter] = useState("all");
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<TrackerInfo | null>(null);
  const [loadingDetail, setLoadingDetail] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [more, setMore] = useState(75);
  const [refreshing, setRefreshing] = useState(false);
  const [source, setSource] = useState("Bundled catalog");

  // A bundled, pinned fallback means the browser doesn't need GitHub access.
  // An explicit refresh obtains new IDs; credentials are never involved.
  async function refresh() {
    setRefreshing(true);
    try {
      const response = await fetch(UPSTREAM_TREE, { headers: { Accept: "application/vnd.github+json" } });
      if (!response.ok) throw Error("GitHub returned HTTP " + response.status);
      const json = await response.json() as { tree?: { path: string; type: string }[]; truncated?: boolean };
      const latest = (json.tree ?? []).filter(x => x.type === "blob" && validPath.test(x.path)).map(x => x.path);
      if (json.truncated || latest.length < 100) throw Error("Catalog response is incomplete");
      setPaths(latest.sort());
      setSource("Updated from upstream");
      setError(null);
    } catch (err) {
      setError("Could not refresh; the bundled catalog is still available. " + (err as Error).message);
    } finally { setRefreshing(false); }
  }

  useEffect(() => {
    if (!selected) return;
    let alive = true;
    setLoadingDetail(true);
    setDetail(null);
    setError(null);
    const controller = new AbortController();
    fetch(UPSTREAM_RAW + selected, { signal: controller.signal })
      .then(res => { if (!res.ok) throw Error("Definition unavailable (HTTP " + res.status + ")"); return res.text(); })
      .then(yaml => { if (alive) setDetail(parseDefinition(yaml, titleFromPath(selected))); })
      .catch(err => { if (alive && err.name !== "AbortError") setError("Unable to load tracker details: " + err.message); })
      .finally(() => { if (alive) setLoadingDetail(false); });
    return () => { alive = false; controller.abort(); };
  }, [selected]);

  const languages = useMemo(() => [...new Set(bundledEntries.map(item => item.language))].sort(), []);
  const results = useMemo(() => {
    const q = filter.trim().toLowerCase();
    return paths.filter(path => {
      const item = metadata.get(path);
      return (!q || path.toLowerCase().includes(q) || (item?.name ?? titleFromPath(path)).toLowerCase().includes(q)) &&
        (privacyFilter === "all" || item?.privacy === privacyFilter) &&
        (languageFilter === "all" || item?.language === languageFilter) &&
        (mediaFilter === "all" || (mediaFilter === "movies" ? item?.movies : item?.tv));
    });
  }, [paths, filter, privacyFilter, languageFilter, mediaFilter]);
  const visible = results.slice(0, more);
  const duplicateCount = paths.length - new Set(paths.map(p => p.split("/").pop())).size;

  return <div className="dialog-backdrop" role="presentation" onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
    <div className="dialog card tracker-catalog-dialog" role="dialog" aria-modal="true" aria-label="Browse tracker catalog">
      <div className="row spread">
        <div><h2>Tracker catalog</h2><p className="muted">Search the directory, inspect authentication requirements, and connect supported sources.</p></div>
        <button onClick={onClose} aria-label="Close tracker catalog">Close</button>
      </div>
      <div className="row tracker-catalog-controls">
        <input aria-label="Search tracker catalog" placeholder="Search trackers…" value={filter} onChange={e => { setFilter(e.target.value); setMore(75); }} />
        <select aria-label="Tracker privacy" value={privacyFilter} onChange={e => { setPrivacyFilter(e.target.value); setMore(75); }}>
          <option value="all">All privacy</option><option value="public">Public</option><option value="private">Private</option><option value="semi-private">Semi-private</option>
        </select>
        <select aria-label="Tracker language" value={languageFilter} onChange={e => { setLanguageFilter(e.target.value); setMore(75); }}>
          <option value="all">All languages</option>{languages.map(language => <option key={language} value={language}>{language}</option>)}
        </select>
        <select aria-label="Tracker categories" value={mediaFilter} onChange={e => { setMediaFilter(e.target.value); setMore(75); }}>
          <option value="all">All categories</option><option value="movies">Movies</option><option value="tv">TV</option>
        </select>
        <button disabled={refreshing} onClick={() => void refresh()}>{refreshing ? "Refreshing…" : "Refresh catalog"}</button>
      </div>
      <p className="muted">{results.length} of {paths.length} definitions · {source} · {duplicateCount > 0 ? duplicateCount + " duplicate filenames" : "Site definitions, not verified connections"} · Source: Prowlarr/Indexers</p>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="tracker-catalog-grid">
        <div className="tracker-catalog-results" aria-label="Tracker results">
          {visible.map(path => <button className={"tracker-catalog-result" + (selected === path ? " selected" : "")} key={path} onClick={() => setSelected(path)}>
            <strong>{metadata.get(path)?.name ?? titleFromPath(path)}</strong>
            <span className="muted">{metadata.get(path)?.privacy ?? "new"} · {metadata.get(path)?.language ?? "?"}</span>
          </button>)}
          {results.length > more && <button onClick={() => setMore(v => v + 75)}>Show more ({results.length - more} remaining)</button>}
          {results.length === 0 && <p className="muted">No matching trackers.</p>}
        </div>
        <div className="tracker-catalog-detail">
          {!selected && <p className="muted">Select a tracker to see its site address, privacy, categories, and configuration requirements.</p>}
          {loadingDetail && <p className="muted">Loading definition…</p>}
          {detail && <div>
            <h3>{detail.name}</h3>
            <p>{detail.description || "No description supplied."}</p>
            <p className="muted">{detail.privacy} · {detail.language || "unspecified language"} · {detail.supportsMovies ? "Movies" : "No movie category"} · {detail.supportsTv ? "TV" : "No TV category"}</p>
            {detail.links.length > 0 && <p><a href={detail.links[0]} target="_blank" rel="noreferrer">Tracker website</a></p>}
            <h3>Configuration required</h3>
            {detail.fields.length ? <ul>{detail.fields.map(f => <li key={f.name}>{f.label} <span className="muted">({f.type})</span></li>)}</ul> : <p className="muted">No site credentials specified in this definition.</p>}
            {selected?.endsWith("/torrentleech.yml") && onNative ? <>
              <p className="muted">Native account login, JSON searches and authenticated torrent downloads are supported for TorrentLeech. Credentials are encrypted in OneArr.</p>
              <button onClick={() => onNative(detail.name, detail.links[0] ?? "https://www.torrentleech.org/", "torrentleech")}>Add TorrentLeech (native)</button>
            </> : selected?.endsWith("/nyaasi.yml") ? <>
              <p className="muted">Native RSS search is supported for this public tracker, without login or Prowlarr.</p>
              <button onClick={() => onGeneric(detail.name, "https://nyaa.si/")}>Add Nyaa (native RSS)</button>
            </> : <>
              <p className="muted">This catalog entry is not yet a working OneArr native adapter. Tracker-specific credentials are shown for reference only and are not collected for unsupported sites.</p>
              <button onClick={() => onGeneric(detail.name)}>Configure an existing Torznab feed</button>
            </>}
          </div>}
        </div>
      </div>
    </div>
  </div>;
}
