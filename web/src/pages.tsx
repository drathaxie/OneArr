import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, ApiError, formatBytes, posterUrl, type ConnectionTestResult, type Job, type MediaDetail, type MediaFile, type MediaItem, type MetadataResult, type QualityProfile, type ReleaseResult } from "./api";
import { LibraryPanel, RootFolderTool } from "./library";

function useLoad<T>(path: string, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const reload = useCallback(() => {
    api<T>("GET", path)
      .then((d) => { setData(d); setError(null); })
      .catch((e: Error) => setError(e.message));
  }, [path]);
  useEffect(() => { reload(); }, [reload, ...deps]);
  return { data, error, reload };
}

function ErrorLine({ message }: { message: string | null }) {
  return message ? <div className="error" role="alert">{message}</div> : null;
}

export function Dashboard() {
  const { data, error } = useLoad<any>("/api/dashboard");
  return (
    <>
      <h2>Dashboard</h2>
      <ErrorLine message={error} />
      {data && !data.tmdbConfigured && (
        <div className="card">
          <strong>TMDb is not configured.</strong> Search and adding titles need a free TMDb API key. Add it under Settings.
        </div>
      )}
      {data && (
        <div className="grid">
          <Stat label="Movies" value={data.movies} />
          <Stat label="Series" value={data.series} />
          <Stat label="Wanted movies" value={data.wantedMovies} />
          <Stat label="Wanted episodes (aired)" value={data.wantedEpisodes} />
          <Stat label="Active downloads" value={data.activeDownloads} />
        </div>
      )}
      {data && (
        <div className="card" style={{ marginTop: "1rem" }}>
          <h2>Recent background jobs</h2>
          {data.recentJobs.length === 0 ? <p className="muted">No jobs have run yet.</p> : (
            <table><thead><tr><th>Job</th><th>Started</th><th>Result</th></tr></thead><tbody>
              {data.recentJobs.map((j: any, i: number) => (
                <tr key={i}><td>{j.name}</td><td>{new Date(j.startedAt).toLocaleString()}</td>
                  <td><span className={j.success ? "badge ok" : "badge bad"}>{j.success ? "ok" : "failed"}</span> {j.message}</td></tr>
              ))}
            </tbody></table>
          )}
        </div>
      )}
    </>
  );
}

function Stat({ label, value }: { label: string; value: number }) {
  return <div className="card"><div className="muted">{label}</div><div className="stat">{value}</div></div>;
}

export function Movies() {
  return <MediaPage kind="movie" />;
}

export function Series() {
  return <MediaPage kind="series" />;
}

function Poster({ item, size }: { item: MediaItem; size?: "card" | "thumb" }) {
  const url = posterUrl(item.posterPath);
  if (!url) return <div className={size === "thumb" ? "poster-thumb poster-fallback" : "poster poster-fallback"}><span>{item.title}</span></div>;
  return <img className={size === "thumb" ? "poster-thumb" : "poster"} src={url} alt={`${item.title} poster`} loading="lazy" />;
}

function MediaPage({ kind }: { kind: "movie" | "series" }) {
  const label = kind === "movie" ? "Movies" : "Series";
  const { data: items, error, reload } = useLoad<MediaItem[]>(`/api/media?type=${kind}`, [kind]);
  const { data: profiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const [filesId, setFilesId] = useState<number | null>(null);
  const [detailId, setDetailId] = useState<number | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [view, setView] = useState<"grid" | "list">(() => (localStorage.getItem("onearr.view") as "grid" | "list") || "grid");
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<"title" | "year" | "status">("title");
  const [filter, setFilter] = useState<"all" | "available" | "missing" | "monitored" | "unmonitored">("all");

  function setViewMode(v: "grid" | "list") {
    setView(v);
    try { localStorage.setItem("onearr.view", v); } catch { /* view preference is best-effort */ }
  }

  async function toggle(item: MediaItem) {
    await api("PATCH", `/api/media/${item.id}`, { monitored: !item.monitored }).catch((e: Error) => setMessage(e.message));
    reload();
  }
  async function remove(item: MediaItem) {
    if (!confirm(`Remove "${item.title}" from OneArr? Files and torrents are not touched.`)) return;
    await api("DELETE", `/api/media/${item.id}`).catch((e: Error) => setMessage(e.message));
    reload();
  }

  const filtered = (items ?? []).filter((item) => {
    if (query && !item.title.toLowerCase().includes(query.toLowerCase())) return false;
    if (filter === "available" && !item.hasFile) return false;
    if (filter === "missing" && item.hasFile) return false;
    if (filter === "monitored" && !item.monitored) return false;
    if (filter === "unmonitored" && item.monitored) return false;
    return true;
  }).sort((a, b) => {
    if (sort === "year") return (b.year ?? 0) - (a.year ?? 0) || a.title.localeCompare(b.title);
    if (sort === "status") return Number(b.hasFile) - Number(a.hasFile) || a.title.localeCompare(b.title);
    return a.title.localeCompare(b.title);
  });

  if (detailId !== null) {
    return <MediaDetail mediaId={detailId} profiles={profiles ?? []} onBack={() => { setDetailId(null); reload(); }} />;
  }

  return (
    <>
      <h2>{label}</h2>
      <AddMedia kind={kind} profiles={profiles ?? []} onAdded={reload} />
      <RootFolderTool kind={kind} onChanged={reload} />
      <ErrorLine message={error ?? message} />
      <div className="card">
        <div className="row">
          <input aria-label={`Search ${label}`} placeholder="Search titles…" value={query} onChange={(e) => setQuery(e.target.value)} style={{ minWidth: 200 }} />
          <select aria-label="Sort" value={sort} onChange={(e) => setSort(e.target.value as "title" | "year" | "status")}>
            <option value="title">Sort: title</option>
            <option value="year">Sort: year (newest)</option>
            <option value="status">Sort: available first</option>
          </select>
          <select aria-label="Filter" value={filter} onChange={(e) => setFilter(e.target.value as typeof filter)}>
            <option value="all">All</option>
            <option value="available">Available</option>
            <option value="missing">Missing</option>
            <option value="monitored">Monitored</option>
            <option value="unmonitored">Unmonitored</option>
          </select>
          <div className="row" style={{ marginLeft: "auto" }}>
            <button className={view === "grid" ? "small primary" : "small"} onClick={() => setViewMode("grid")}>Posters</button>
            <button className={view === "list" ? "small primary" : "small"} onClick={() => setViewMode("list")}>List</button>
          </div>
        </div>
        <p className="muted">Showing {filtered.length} of {(items ?? []).length}.</p>
      </div>
      {items && items.length === 0 && <p className="muted">Nothing here yet. Search TMDb above to add a {kind === "movie" ? "movie" : "series"}.</p>}
      {items && items.length > 0 && filtered.length === 0 && <p className="muted">No titles match the current search and filter.</p>}
      {view === "grid" && filtered.length > 0 && (
        <div className="poster-grid">
          {filtered.map((item) => (
            <article key={item.id} className="poster-card">
              <button className="poster-button" onClick={() => setDetailId(item.id)} aria-label={`Open ${item.title}`}>
                <Poster item={item} />
              </button>
              <div className="poster-info">
                <strong className="poster-title" onClick={() => setDetailId(item.id)} style={{ cursor: "pointer" }}>{item.title}</strong>
                <div className="muted">{item.year ?? ""}</div>
                <div className="row" style={{ marginTop: ".25rem" }}>
                  <span className={item.hasFile ? "badge ok" : "badge bad"}>{item.hasFile ? "Available" : "Missing"}</span>
                  <span className="badge">{item.monitored ? "Monitored" : "Unmonitored"}</span>
                </div>
                <div className="muted" style={{ fontSize: ".85rem" }}>{profiles?.find((p) => p.id === item.qualityProfileId)?.name ?? item.qualityProfileId}</div>
                <div className="row" style={{ marginTop: ".25rem" }}>
                  <button className="small" onClick={() => toggle(item)}>{item.monitored ? "Unmonitor" : "Monitor"}</button>
                  <button className="small" onClick={() => setDetailId(item.id)}>Details</button>
                </div>
              </div>
            </article>
          ))}
        </div>
      )}
      {view === "list" && filtered.length > 0 && (
        <div className="card table-wrap">
          <table>
            <thead><tr><th /><th>Title</th><th>Year</th><th>Monitored</th><th>Profile</th><th>File</th><th /></tr></thead>
            <tbody>
              {filtered.map((item) => (
                <tr key={item.id}>
                  <td><Poster item={item} size="thumb" /></td>
                  <td><button className="small" style={{ border: 0, padding: 0, fontWeight: 600 }} onClick={() => setDetailId(item.id)}>{item.title}</button></td>
                  <td>{item.year ?? ""}</td>
                  <td><button className="small" onClick={() => toggle(item)}>{item.monitored ? "Yes" : "No"}</button></td>
                  <td>{profiles?.find((p) => p.id === item.qualityProfileId)?.name ?? item.qualityProfileId}</td>
                  <td>{item.hasFile ? <span className="badge ok">have</span> : <span className="badge">missing</span>}</td>
                  <td className="row">
                    <button className="small" onClick={() => setDetailId(item.id)}>Details</button>
                    <button className="small" onClick={() => setFilesId(filesId === item.id ? null : item.id)}>Files</button>
                    <button className="small danger" onClick={() => remove(item)}>Remove</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {filesId !== null && <FileList mediaId={filesId} onClose={() => setFilesId(null)} />}
    </>
  );
}

function MediaDetail({ mediaId, profiles, onBack }: { mediaId: number; profiles: QualityProfile[]; onBack: () => void }) {
  const { data, error, reload } = useLoad<MediaDetail>(`/api/media/${mediaId}/detail`, [mediaId]);
  const [message, setMessage] = useState<string | null>(null);
  const [root, setRoot] = useState<string | null>(null);
  const [showReleases, setShowReleases] = useState(false);
  const [releaseEpisode, setReleaseEpisode] = useState<number | null>(null);
  const [collapsed, setCollapsed] = useState<Set<number>>(new Set());

  async function patch(body: Record<string, unknown>, after?: () => void) {
    setMessage(null);
    try {
      await api("PATCH", `/api/media/${mediaId}`, body);
      reload(); after?.();
    } catch (e) { setMessage((e as Error).message); }
  }

  function toggleSeason(seasonNumber: number, monitored: boolean) {
    api("PATCH", `/api/media/${mediaId}/seasons/${seasonNumber}`, { monitored })
      .then(() => reload()).catch((e: Error) => setMessage(e.message));
  }
  function toggleEpisode(episodeId: number, monitored: boolean) {
    api("PATCH", `/api/media/episodes/${episodeId}`, { monitored })
      .then(() => reload()).catch((e: Error) => setMessage(e.message));
  }
  function toggleCollapsed(seasonNumber: number) {
    const next = new Set(collapsed);
    if (next.has(seasonNumber)) next.delete(seasonNumber); else next.add(seasonNumber);
    setCollapsed(next);
  }

  if (!data) return <><button onClick={onBack}>← Back</button><ErrorLine message={error} /></>;
  const isSeries = data.mediaType === "Series";
  const totalEpisodes = data.episodes.length;
  const haveEpisodes = data.episodes.filter((e) => e.hasFile).length;
  const wantedEpisodes = data.seasons.reduce((n, s) => n + s.wantedCount, 0);
  const fileStatus = !isSeries
    ? (data.hasFile ? "Available" : "Missing")
    : totalEpisodes === 0 ? "No episodes" : haveEpisodes === totalEpisodes ? "Available" : haveEpisodes === 0 ? "Missing" : "Partial";

  return (
    <>
      <button onClick={onBack}>← Back to {isSeries ? "Series" : "Movies"}</button>
      <ErrorLine message={error ?? message} />
      <div className="card detail-head">
        {posterUrl(data.posterPath)
          ? <img className="poster detail-poster" src={posterUrl(data.posterPath)!} alt={`${data.title} poster`} />
          : <div className="poster poster-fallback detail-poster"><span>{data.title}</span></div>}
        <div style={{ minWidth: 0 }}>
          <h2>{data.title} {data.year ? `(${data.year})` : ""}</h2>
          <p>{data.overview || <span className="muted">No overview available.</span>}</p>
          <div className="row">
            <span className={fileStatus === "Available" ? "badge ok" : fileStatus === "Missing" ? "badge bad" : "badge"}>{fileStatus}</span>
            <span className="badge">{data.monitored ? "Monitored" : "Unmonitored"}</span>
            <span className="badge">{profiles.find((p) => p.id === data.qualityProfileId)?.name ?? data.qualityProfileId}</span>
            {isSeries && <span className="badge">Wanted episodes: {wantedEpisodes}</span>}
          </div>
          <p className="muted">Root folder: <code>{data.rootFolder}</code>{data.legacyPath ? <> · imported from <code>{data.legacyPath}</code> ({data.legacySource})</> : ""}</p>
          <div className="row">
            <button className="small" onClick={() => patch({ monitored: !data.monitored })}>{data.monitored ? "Unmonitor" : "Monitor"}</button>
            <button className="small" onClick={() => setShowReleases(!showReleases)}>{showReleases ? "Hide releases" : "Search releases"}</button>
          </div>
          <form className="row" style={{ marginTop: ".5rem" }} onSubmit={(e) => { e.preventDefault(); if (root !== null) patch({ rootFolder: root }); }}>
            <label>Root folder
              <input value={root ?? data.rootFolder} onChange={(e) => setRoot(e.target.value)} style={{ minWidth: 280 }} />
            </label>
            <button className="small primary" type="submit">Save root folder</button>
          </form>
          <form className="row" onSubmit={(e) => { e.preventDefault(); }}>
            <label>Quality profile
              <select value={data.qualityProfileId} onChange={(e) => patch({ qualityProfileId: Number(e.target.value) })}>
                {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </label>
          </form>
        </div>
      </div>
      {showReleases && !isSeries && <ReleasePanel mediaId={mediaId} episodeId={null} onClose={() => setShowReleases(false)} />}
      {isSeries && data.seasons.map((s) => {
        const eps = data.episodes.filter((e) => e.seasonNumber === s.seasonNumber);
        const isCollapsed = collapsed.has(s.seasonNumber);
        return (
          <div key={s.seasonNumber} className="card">
            <div className="row spread">
              <h2 style={{ margin: 0 }}>Season {s.seasonNumber}</h2>
              <div className="row">
                <span className="badge">{s.monitored ? "Monitored" : "Unmonitored"}</span>
                <span className="muted">{s.hasFileCount}/{s.episodeCount} have files · {s.wantedCount} wanted</span>
                <button className="small" onClick={() => toggleSeason(s.seasonNumber, !s.monitored)}>{s.monitored ? "Unmonitor season" : "Monitor season"}</button>
                <button className="small" onClick={() => toggleCollapsed(s.seasonNumber)}>{isCollapsed ? "Expand" : "Collapse"}</button>
              </div>
            </div>
            {!isCollapsed && (
              <div className="table-wrap"><table>
                <thead><tr><th>Episode</th><th>Title</th><th>Air date</th><th>Monitored</th><th>File</th><th>Quality</th><th /></tr></thead>
                <tbody>
                  {eps.map((e) => (
                    <tr key={e.id}>
                      <td>S{String(e.seasonNumber).padStart(2, "0")}E{String(e.episodeNumber).padStart(2, "0")}</td>
                      <td>{e.title}</td>
                      <td>{e.airDate ?? ""}</td>
                      <td><button className="small" onClick={() => toggleEpisode(e.id, !e.monitored)}>{e.monitored ? "Yes" : "No"}</button></td>
                      <td>{e.hasFile ? <span className="badge ok">have</span> : <span className="badge">missing</span>}</td>
                      <td>{e.resolution ? `${e.resolution}p` : "—"}</td>
                      <td><button className="small" onClick={() => setReleaseEpisode(releaseEpisode === e.id ? null : e.id)}>Releases</button></td>
                    </tr>
                  ))}
                </tbody>
              </table></div>
            )}
            {releaseEpisode !== null && eps.some((e) => e.id === releaseEpisode) && (
              <ReleasePanel mediaId={mediaId} episodeId={releaseEpisode} onClose={() => setReleaseEpisode(null)} />
            )}
          </div>
        );
      })}
      <div className="card">
        <h2>Linked files ({data.files.length})</h2>
        {data.files.length === 0 ? <p className="muted">No linked files yet. Run a library scan in Settings, or import a download, and they will appear here.</p> : (
          <div className="table-wrap"><table>
            <thead><tr><th>File</th><th>Size</th><th>Resolution</th><th>Status</th><th>Matched by</th></tr></thead>
            <tbody>{data.files.map((f) => (
              <tr key={f.path}>
                <td className="muted">{f.path}</td>
                <td>{formatBytes(f.sizeBytes)}</td>
                <td>{f.resolution ? `${f.resolution}p` : "—"}</td>
                <td>{f.status}</td>
                <td>{f.matchMethod}</td>
              </tr>
            ))}</tbody>
          </table></div>
        )}
      </div>
    </>
  );
}

function AddMedia({ kind, profiles, onAdded }: { kind: "movie" | "series"; profiles: QualityProfile[]; onAdded: () => void }) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<MetadataResult[]>([]);
  const [profileId, setProfileId] = useState<number | "">("");
  const [root, setRoot] = useState(() => localStorage.getItem("onearr.root") ?? "");
  const [monitored, setMonitored] = useState(true);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function search(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      setResults(await api<MetadataResult[]>("GET", `/api/metadata/search?type=${kind}&q=${encodeURIComponent(query)}`));
    } catch (err) {
      setMessage((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function add(r: MetadataResult) {
    const pid = profileId === "" ? profiles[0]?.id : profileId;
    if (!pid) return setMessage("Create a quality profile in Settings first");
    if (!/^([A-Za-z]:[\\/]|\\\\|\/)/.test(root)) return setMessage("Enter a folder path, e.g. D:\\Media\\Movies");
    try {
      localStorage.setItem("onearr.root", root);
    } catch { /* storage unavailable */ }
    setBusy(true);
    try {
      const res = await api<{ created: boolean; item: MediaItem }>("POST", "/api/media", {
        type: kind, providerId: r.providerId, qualityProfileId: pid, rootFolder: root, monitored, seasons: null, allSeasons: true,
      });
      setMessage(res.created ? `Added ${r.title}` : `${r.title} is already in OneArr`);
      onAdded();
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : (err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="card">
      <form className="row" onSubmit={search}>
        <input aria-label="Search TMDb" placeholder={`Search TMDb for a ${kind === "movie" ? "movie" : "series"}`} value={query} onChange={(e) => setQuery(e.target.value)} style={{ flex: 1, minWidth: 220 }} />
        <button className="primary" type="submit" disabled={busy || !query.trim()}>Search</button>
      </form>
      <div className="row" style={{ marginTop: ".5rem" }}>
        <label style={{ minWidth: 200 }}>Root folder
          <input aria-label="Root folder" value={root} onChange={(e) => setRoot(e.target.value)} placeholder="D:\\Media\\Movies" />
        </label>
        <label>Quality profile
          <select value={profileId} onChange={(e) => setProfileId(e.target.value ? Number(e.target.value) : "")}>
            {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
        </label>
        <label className="inline"><input type="checkbox" checked={monitored} onChange={(e) => setMonitored(e.target.checked)} /> Monitor</label>
      </div>
      <ErrorLine message={message} />
      {results.length > 0 && (
        <ul className="results">
          {results.slice(0, 15).map((r) => (
            <li key={r.providerId} className="row spread" style={{ padding: ".3rem 0" }}>
              <span>{r.title}{r.year ? ` (${r.year})` : ""} <span className="muted">#{r.providerId}</span></span>
              <button className="small" disabled={busy} onClick={() => add(r)}>Add</button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function FileList({ mediaId, onClose }: { mediaId: number; onClose: () => void }) {
  const { data, error } = useLoad<{ itemId: number; title: string; rootFolder: string; hasFile: boolean; files: MediaFile[] }>(`/api/media/${mediaId}/files`, [mediaId]);
  return (
    <div className="card" style={{ marginTop: "1rem" }}>
      <div className="row spread">
        <h2>Linked files</h2>
        <button onClick={onClose}>Close</button>
      </div>
      <p className="muted">Files OneArr has recorded for this item, from a library scan or an import. This list does not scan the disk.</p>
      <ErrorLine message={error} />
      {data && (
        <>
          <p className="muted">Root folder: <code>{data.rootFolder || "—"}</code> · {data.hasFile ? "has file" : "missing"}</p>
          {data.files.length === 0 ? <p className="muted">No linked files yet. Run a library scan in Settings, or import a download, and they will appear here.</p> : (
            <div className="table-wrap"><table>
              <thead><tr><th>File</th><th>Size</th><th>Resolution</th><th>Status</th><th>Matched by</th></tr></thead>
              <tbody>{data.files.map((f) => (
                <tr key={f.path}>
                  <td className="muted">{f.path}</td>
                  <td>{formatBytes(f.sizeBytes)}</td>
                  <td>{f.resolution ? `${f.resolution}p` : "—"}</td>
                  <td>{f.status}</td>
                  <td>{f.matchMethod}</td>
                </tr>
              ))}</tbody>
            </table></div>
          )}
        </>
      )}
    </div>
  );
}

export function ReleasePanel({ mediaId, episodeId, onClose }: { mediaId: number; episodeId: number | null; onClose: () => void }) {
  const [busy, setBusy] = useState(false);
  const [results, setResults] = useState<ReleaseResult[] | null>(null);
  const [errors, setErrors] = useState<{ indexer: string; message: string }[]>([]);
  const [message, setMessage] = useState<string | null>(null);

  async function search() {
    setBusy(true);
    setMessage(null);
    try {
      const res = await api<{ results: ReleaseResult[]; indexerErrors: { indexer: string; message: string }[] }>("POST", "/api/search", { mediaItemId: mediaId, episodeId });
      setResults(res.results);
      setErrors(res.indexerErrors);
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function grab(r: ReleaseResult) {
    try {
      await api("POST", "/api/grab", { mediaItemId: mediaId, episodeId, title: r.title, link: r.link, infoHash: r.infoHash });
      setMessage(`Sent to qBittorrent: ${r.title}`);
    } catch (e) {
      setMessage((e as Error).message);
    }
  }

  return (
    <div className="card" style={{ marginTop: "1rem" }}>
      <div className="row spread">
        <h2>Manual release search</h2>
        <div className="row">
          <button className="primary" onClick={search} disabled={busy}>{busy ? "Searching…" : "Search indexers"}</button>
          <button onClick={onClose}>Close</button>
        </div>
      </div>
      <p className="muted">Automatic grabbing is off. Only releases you choose here are sent to qBittorrent.</p>
      <ErrorLine message={message} />
      {errors.map((e, i) => <div key={i} className="error">Indexer “{e.indexer}”: {e.message}</div>)}
      {results && results.length === 0 && <p className="muted">No releases returned.</p>}
      {results && results.length > 0 && (
        <div className="table-wrap"><table>
          <thead><tr><th>Release</th><th>Indexer</th><th>Res</th><th>Src</th><th>Seeds</th><th>Size</th><th>Decision</th><th /></tr></thead>
          <tbody>
            {results.map((r, i) => (
              <tr key={i}>
                <td>{r.title}</td>
                <td>{r.indexer}</td>
                <td>{r.resolution ?? "?"}p</td>
                <td>{r.source}</td>
                <td>{r.seeders}</td>
                <td>{(r.size / 1_073_741_824).toFixed(2)} GiB</td>
                <td>
                  <span className={r.accepted ? "badge ok" : "badge bad"}>{r.accepted ? `accepted (${r.score})` : "rejected"}</span>
                  {r.reasons.length > 0 && <ul className="reasons">{r.reasons.map((x, j) => <li key={j}>{x}</li>)}</ul>}
                </td>
                <td><button className="small" disabled={!r.accepted} onClick={() => grab(r)}>Grab</button></td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </div>
  );
}

export function Wanted() {
  const { data, error, reload } = useLoad<any>("/api/wanted");
  const { data: profiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const [detailId, setDetailId] = useState<number | null>(null);
  const [typeFilter, setTypeFilter] = useState<"all" | "movies" | "episodes">("all");
  const [downloadFilter, setDownloadFilter] = useState<"all" | "active" | "none">("all");
  const [query, setQuery] = useState("");
  const [collapsed, setCollapsed] = useState<Set<number>>(new Set());

  if (detailId !== null) {
    return <MediaDetail mediaId={detailId} profiles={profiles ?? []} onBack={() => { setDetailId(null); reload(); }} />;
  }
  if (!data) return <><h2>Wanted</h2><ErrorLine message={error} /></>;

  const movies = (data.movies as MediaItem[]).filter((m) =>
    (typeFilter === "all" || typeFilter === "movies") &&
    (!query || m.title.toLowerCase().includes(query.toLowerCase())) &&
    (downloadFilter === "all" || (downloadFilter === "active") === !!data.movieHasActiveDownload?.[String(m.id)]));
  const episodes = (data.episodes as any[]).filter((e) =>
    (typeFilter === "all" || typeFilter === "episodes") &&
    (!query || e.seriesTitle.toLowerCase().includes(query.toLowerCase()) || e.episodeTitle.toLowerCase().includes(query.toLowerCase())) &&
    (downloadFilter === "all" || (downloadFilter === "active") === !!e.hasActiveDownload));
  const bySeries = new Map<number, { title: string; items: any[] }>();
  for (const e of episodes) {
    const group: { title: string; items: any[] } = bySeries.get(e.mediaItemId) ?? { title: e.seriesTitle, items: [] };
    group.items.push(e);
    bySeries.set(e.mediaItemId, group);
  }
  function toggleSeries(id: number) {
    const next = new Set(collapsed);
    if (next.has(id)) next.delete(id); else next.add(id);
    setCollapsed(next);
  }

  return (
    <>
      <h2>Wanted</h2>
      <ErrorLine message={error} />
      <div className="card">
        <p className="muted">
          Wanted lists only monitored items with no file: {data.movies.length} movie(s) and {data.episodes.length} aired episode(s).
          It does not include unmonitored material — {data.unmonitoredMoviesWithoutFile} movie(s) and {data.unmonitoredEpisodesWithoutFile} episode(s)
          without a file are deliberately unmonitored, not wanted. If those numbers look wrong, check the series or season
          monitored state in its detail view, or run the monitoring sync on Migration.
        </p>
        <div className="row">
          <input aria-label="Search wanted" placeholder="Search titles…" value={query} onChange={(e) => setQuery(e.target.value)} style={{ minWidth: 200 }} />
          <select aria-label="Wanted type" value={typeFilter} onChange={(e) => setTypeFilter(e.target.value as typeof typeFilter)}>
            <option value="all">Movies + episodes</option>
            <option value="movies">Movies only</option>
            <option value="episodes">Episodes only</option>
          </select>
          <select aria-label="Download state" value={downloadFilter} onChange={(e) => setDownloadFilter(e.target.value as typeof downloadFilter)}>
            <option value="all">Any download state</option>
            <option value="active">Download in progress</option>
            <option value="none">No download in progress</option>
          </select>
        </div>
      </div>
      {(typeFilter === "all" || typeFilter === "movies") && (
        <div className="card">
          <h2>Movies without a file ({movies.length})</h2>
          {movies.length === 0 ? <p className="muted">None matching.</p> : (
            <ul>{movies.map((m) => (
              <li key={m.id}>
                <button className="small" style={{ border: 0, padding: 0, fontWeight: 600 }} onClick={() => setDetailId(m.id)}>{m.title} {m.year ? `(${m.year})` : ""}</button>
                {" "}— no file · {data.movieHasActiveDownload?.[String(m.id)] ? "download in progress" : "no download in progress"}
              </li>
            ))}</ul>
          )}
        </div>
      )}
      {(typeFilter === "all" || typeFilter === "episodes") && (
        <div className="card">
          <h2>Aired episodes without a file, by series ({episodes.length})</h2>
          {episodes.length === 0 ? <p className="muted">None matching.</p> : [...bySeries.entries()].map(([seriesId, group]) => (
            <div key={seriesId} style={{ marginBottom: ".75rem" }}>
              <div className="row spread">
                <button className="small" style={{ border: 0, padding: 0, fontWeight: 600 }} onClick={() => setDetailId(seriesId)}>{group.title} ({group.items.length})</button>
                <button className="small" onClick={() => toggleSeries(seriesId)}>{collapsed.has(seriesId) ? "Expand" : "Collapse"}</button>
              </div>
              {!collapsed.has(seriesId) && (
                <div className="table-wrap"><table><thead><tr><th>Episode</th><th>Title</th><th>Aired</th><th>Why wanted</th><th>Download</th></tr></thead><tbody>
                  {group.items.map((e) => (
                    <tr key={e.episodeId}>
                      <td>S{String(e.seasonNumber).padStart(2, "0")}E{String(e.episodeNumber).padStart(2, "0")}</td>
                      <td>{e.episodeTitle}</td>
                      <td>{e.airDate}</td>
                      <td>No file (monitored, aired)</td>
                      <td>{e.hasActiveDownload ? "In progress" : "None"}</td>
                    </tr>
                  ))}
                </tbody></table></div>
              )}
            </div>
          ))}
        </div>
      )}
      <p className="muted">
        Why wanted is always "no file" here: Wanted is computed from monitored, aired items with no linked file.
        Files that are unmatched, ambiguous, sample-only, misnumbered, or under an inaccessible folder do not appear
        as wanted — they appear in the Library scan review in Settings, where they can be matched or acknowledged.
      </p>
    </>
  );
}

export function Queue() {
  const { data: jobs, error, reload } = useLoad<Job[]>("/api/queue");
  const { data: imports } = useLoad<any[]>("/api/imports");
  const [message, setMessage] = useState<string | null>(null);

  async function run(label: string, path: string, confirmText?: string) {
    if (confirmText && !confirm(confirmText)) return;
    setMessage(null);
    try {
      const res = await api<any>("POST", path);
      setMessage(res?.message ?? `${label} finished`);
      reload();
    } catch (e) {
      setMessage((e as Error).message);
    }
  }

  return (
    <>
      <h2>Queue</h2>
      <div className="row">
        <button onClick={() => run("Sync", "/api/queue/sync")}>Sync from qBittorrent</button>
        <button onClick={() => run("Dry run", "/api/import?dryRun=true")}>Import dry run</button>
        <button className="danger" onClick={() => run("Import", "/api/import?dryRun=false", "Run a real import now? Files are hardlinked or verified-copied; source torrents are never deleted.")}>Import now</button>
      </div>
      <ErrorLine message={error ?? message} />
      <div className="card table-wrap">
        {jobs && jobs.length === 0 && <p className="muted">No downloads yet. Grab a release from the Movies or Series pages.</p>}
        {jobs && jobs.length > 0 && (
          <table>
            <thead><tr><th>Release</th><th>Status</th><th>Client state</th><th>Progress</th><th>Category</th><th>Hash</th><th /></tr></thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id}>
                  <td>{j.releaseTitle}{j.error && <div className="error">{j.error}</div>}</td>
                  <td>{j.status}</td>
                  <td>{j.clientState || "—"}</td>
                  <td>{Math.round(j.progress * 100)}%</td>
                  <td>{j.category}</td>
                  <td className="muted">{j.hash ? j.hash.slice(0, 10) : "—"}</td>
                  <td>{j.status === "Missing" && <button className="small" onClick={() => run("Clear missing", `/api/queue/${j.id}/clear-missing`, "Clear this missing download? It only changes OneArr's record to Failed so the item can be searched or grabbed again. No torrent or file is touched.")}>Clear missing</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      <div className="card table-wrap">
        <h2>Import records</h2>
        {imports && imports.length === 0 && <p className="muted">No import activity.</p>}
        {imports && imports.length > 0 && (
          <table><thead><tr><th>Action</th><th>Result</th><th>Destination</th><th>Message</th></tr></thead><tbody>
            {imports.map((r) => (
              <tr key={r.id}><td>{r.action}</td><td>{r.success ? <span className="badge ok">ok</span> : <span className="badge bad">failed</span>}</td><td className="muted">{r.destinationPath}</td><td>{r.message}</td></tr>
            ))}
          </tbody></table>
        )}
      </div>
    </>
  );
}

function ConnectionTests({ indexers, clients }: { indexers: any[]; clients: any[] }) {
  const [result, setResult] = useState<ConnectionTestResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  async function test(label: string, body: Record<string, unknown>) {
    setBusy(label); setMessage(null);
    try {
      setResult(await api<ConnectionTestResult>("POST", "/api/connections/test", body));
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="card">
      <h2>Connection tests</h2>
      <p className="muted">Read-only checks. Nothing is downloaded, imported, or written to another application, and no key or password is shown back.</p>
      <div className="row">
        <button disabled={busy !== null} onClick={() => test("tmdb", { service: "tmdb" })}>{busy === "tmdb" ? "Testing…" : "Test TMDb"}</button>
        {indexers.map((i: any) => (
          <button key={i.id} disabled={busy !== null} onClick={() => test(`indexer-${i.id}`, { service: "torznab", indexerId: i.id })}>Test indexer: {i.name}</button>
        ))}
        {clients.map((c: any) => (
          <button key={c.id} disabled={busy !== null} onClick={() => test(`client-${c.id}`, { service: "qbittorrent", clientId: c.id })}>Test qBittorrent: {c.name}</button>
        ))}
      </div>
      <ErrorLine message={message} />
      {result && (
        <div>
          <p className={result.ok ? "ok" : "error"}>{result.detail}</p>
          {result.categories && result.categories.length > 0 && (
            <p className="muted">Categories: {result.categories.slice(0, 20).map((c) => `${c.id} ${c.name}`).join(", ")}
              {result.categories.length > 20 ? ` (+${result.categories.length - 20} more)` : ""} · search: {result.searchAvailable ? "yes" : "no"}, TV search: {result.tvSearchAvailable ? "yes" : "no"}, movie search: {result.movieSearchAvailable ? "yes" : "no"}</p>
          )}
          {result.states && <p className="muted">Torrent states: {result.states.map((s) => `${s.state}: ${s.count}`).join(", ")} · content paths reported: {result.anyContentPath ? "yes" : "no"}</p>}
        </div>
      )}
    </div>
  );
}

function AutomationStatus() {
  const { data: settings } = useLoad<any>("/api/settings");
  const { data: status } = useLoad<any>("/api/library/status");
  if (!settings || !status) return null;
  const on = settings.automaticGrabbing === true;
  const gateOk = status.automation?.allowed === true;
  return (
    <div className="card">
      <h2>Automation</h2>
      <p className={on && gateOk ? "ok" : "error"}>
        Automatic grabbing is {on ? "ON" : "OFF"}. {gateOk ? "Library reconciled." : `Blocked: ${status.automation?.reason ?? "library not reconciled"}`}
      </p>
      <p className="muted">
        {on && !gateOk
          ? "The toggle is on, but automatic searches are still paused until the library check passes. That is the safety gate, not a bug: apply a complete library scan in the Library section below, and make sure every folder is reachable and every unmatched file is reviewed."
          : !on
            ? "Automatic grabbing stays off until you turn it on in General below. Even then, the library gate above must pass before any automatic search runs."
            : "Automatic searches may run for monitored wanted items with no file and no active or missing download."}
      </p>
    </div>
  );
}

export function Settings() {
  const { data, error, reload } = useLoad<any>("/api/settings");
  const { data: profiles, reload: reloadProfiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const { data: indexers, reload: reloadIndexers } = useLoad<any[]>("/api/indexers");
  const { data: clients, reload: reloadClients } = useLoad<any[]>("/api/download-clients");
  const [message, setMessage] = useState<string | null>(null);

  async function save(body: Record<string, unknown>) {
    setMessage(null);
    try {
      await api("PUT", "/api/settings", body);
      setMessage("Saved");
      reload();
    } catch (e) {
      setMessage((e as Error).message);
    }
  }

  return (
    <>
      <h2>Settings</h2>
      <ErrorLine message={error ?? message} />
      <AutomationStatus />
      {data && <GeneralForm data={data} onSave={save} />}
      <ConnectionTests indexers={indexers ?? []} clients={clients ?? []} />
      <LibraryPanel />

      <div className="card">
        <h2>Quality profiles</h2>
        <ul>{profiles?.map((p) => <li key={p.id}>{p.name}: minimum {p.minResolution}p, cutoff {p.cutoffResolution}p {p.allowWebSources ? "" : "(no web sources)"}</li>)}</ul>
        <ProfileForm onCreated={reloadProfiles} />
      </div>

      <div className="card">
        <h2>Indexers (Torznab / Newznab)</h2>
        <p className="muted">Keys are stored server-side and never shown again.</p>
        <ul>{indexers?.map((i) => <li key={i.id} className="row spread">{i.name} <span className="muted">{i.baseUrl}</span> <button className="small danger" onClick={async () => { await api("DELETE", `/api/indexers/${i.id}`); reloadIndexers(); }}>Delete</button></li>)}</ul>
        <IndexerForm onCreated={reloadIndexers} />
      </div>

      <div className="card">
        <h2>Download clients (qBittorrent)</h2>
        <p className="muted">Credentials are stored server-side and never returned to the browser.</p>
        <ul>{clients?.map((c) => <li key={c.id} className="row spread">{c.name} <span className="muted">{c.baseUrl} · {c.movieCategory} / {c.tvCategory}</span> <button className="small danger" onClick={async () => { await api("DELETE", `/api/download-clients/${c.id}`); reloadClients(); }}>Delete</button></li>)}</ul>
        <ClientForm onCreated={reloadClients} />
      </div>
    </>
  );
}

function GeneralForm({ data, onSave }: { data: any; onSave: (body: Record<string, unknown>) => void }) {
  const [tmdb, setTmdb] = useState("");
  const [torrentRoot, setTorrentRoot] = useState(data.torrentRoot ?? "");
  const [mediaRoot, setMediaRoot] = useState(data.mediaRoot ?? "");
  const [dry, setDry] = useState<boolean>(data.dryRunImport);
  const [auto, setAuto] = useState<boolean>(data.automaticGrabbing ?? false);
  return (
    <form className="card" onSubmit={(e) => { e.preventDefault(); onSave({ tmdbApiKey: tmdb || undefined, torrentRoot, mediaRoot, dryRunImport: dry, automaticGrabbing: auto }); setTmdb(""); }}>
      <h2>General</h2>
      <label>TMDb API key {data.tmdbConfigured ? <span className="ok">configured</span> : <span className="error">not set</span>}
        <input type="password" autoComplete="off" value={tmdb} onChange={(e) => setTmdb(e.target.value)} placeholder={data.tmdbConfigured ? "Leave blank to keep the stored key" : "Paste your TMDb v3 key or v4 read token"} />
      </label>
      <label>Torrent download root (absolute)
        <input value={torrentRoot} onChange={(e) => setTorrentRoot(e.target.value)} placeholder="D:\\Downloads\\Torrents" />
      </label>
      <label>Media root (absolute, imports go here)
        <input value={mediaRoot} onChange={(e) => setMediaRoot(e.target.value)} placeholder="D:\\Media" />
      </label>
      <label className="inline"><input type="checkbox" checked={dry} onChange={(e) => setDry(e.target.checked)} /> Imports are dry-run only (recommended until verified)</label>
      <label className="inline"><input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /> Automatic grabbing (off by default)</label>
      <p className="muted">Turning this on does not bypass the library check: automatic searches only run when the Library panel shows the library as reconciled, every folder is reachable, and no download is already active for an item. Until then, all grabs are manual. Keep it off during testing unless you have applied a complete library scan.</p>
      <button className="primary" type="submit">Save</button>
    </form>
  );
}

function ProfileForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("");
  const [min, setMin] = useState(720);
  const [cutoff, setCutoff] = useState(1080);
  const [web, setWeb] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  return (
    <form className="row" onSubmit={async (e) => {
      e.preventDefault(); setErr(null);
      try { await api("POST", "/api/quality-profiles", { name, minResolution: min, cutoffResolution: cutoff, allowWebSources: web }); setName(""); onCreated(); }
      catch (x) { setErr((x as Error).message); }
    }}>
      <input aria-label="Profile name" placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
      <select aria-label="Minimum resolution" value={min} onChange={(e) => setMin(Number(e.target.value))}>{[480, 720, 1080, 2160].map((r) => <option key={r} value={r}>min {r}p</option>)}</select>
      <select aria-label="Cutoff resolution" value={cutoff} onChange={(e) => setCutoff(Number(e.target.value))}>{[480, 720, 1080, 2160].map((r) => <option key={r} value={r}>cutoff {r}p</option>)}</select>
      <label className="inline"><input type="checkbox" checked={web} onChange={(e) => setWeb(e.target.checked)} /> allow web sources</label>
      <button type="submit">Add profile</button>
      <ErrorLine message={err} />
    </form>
  );
}

function IndexerForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [key, setKey] = useState("");
  const [err, setErr] = useState<string | null>(null);
  return (
    <form className="row" onSubmit={async (e) => {
      e.preventDefault(); setErr(null);
      try { await api("POST", "/api/indexers", { name, baseUrl: url, apiKey: key, enabled: true }); setName(""); setUrl(""); setKey(""); onCreated(); }
      catch (x) { setErr((x as Error).message); }
    }}>
      <input aria-label="Indexer name" placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
      <input aria-label="Indexer URL" placeholder="https://indexer.example/api/…torznab" value={url} onChange={(e) => setUrl(e.target.value)} required style={{ minWidth: 260 }} />
      <input aria-label="Indexer API key" type="password" autoComplete="off" placeholder="API key" value={key} onChange={(e) => setKey(e.target.value)} />
      <button type="submit">Add indexer</button>
      <ErrorLine message={err} />
    </form>
  );
}

function ClientForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("qBittorrent");
  const [url, setUrl] = useState("");
  const [user, setUser] = useState("");
  const [pass, setPass] = useState("");
  const [movie, setMovie] = useState("onearr-movies");
  const [tv, setTv] = useState("onearr-tv");
  const [err, setErr] = useState<string | null>(null);
  return (
    <form className="row" onSubmit={async (e) => {
      e.preventDefault(); setErr(null);
      try { await api("POST", "/api/download-clients", { name, baseUrl: url, username: user, password: pass, movieCategory: movie, tvCategory: tv, enabled: true }); setPass(""); onCreated(); }
      catch (x) { setErr((x as Error).message); }
    }}>
      <input aria-label="Client name" value={name} onChange={(e) => setName(e.target.value)} required />
      <input aria-label="Client URL" placeholder="http://qbittorrent:8080" value={url} onChange={(e) => setUrl(e.target.value)} required style={{ minWidth: 220 }} />
      <input aria-label="Client username" value={user} onChange={(e) => setUser(e.target.value)} />
      <input aria-label="Client password" type="password" autoComplete="off" value={pass} onChange={(e) => setPass(e.target.value)} />
      <input aria-label="Movie category" value={movie} onChange={(e) => setMovie(e.target.value)} />
      <input aria-label="TV category" value={tv} onChange={(e) => setTv(e.target.value)} />
      <button type="submit">Add client</button>
      <ErrorLine message={err} />
    </form>
  );
}

export function Migration() {
  const [kind, setKind] = useState<"sonarr" | "radarr">("radarr");
  const [url, setUrl] = useState("");
  const [key, setKey] = useState("");
  const [preview, setPreview] = useState<any[] | null>(null);
  const [testResult, setTestResult] = useState<ConnectionTestResult | null>(null);
  const [errors, setErrors] = useState<{ title: string; error: string }[]>([]);
  const [picked, setPicked] = useState<Set<number>>(new Set());
  const { data: profiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const [profileId, setProfileId] = useState<number | "">("");
  const [root, setRoot] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [syncResult, setSyncResult] = useState<any | null>(null);
  // The preview Apply is bound to: same kind + URL, and the exact result the user saw.
  // Changing kind, URL, or key clears it, so Apply can never run on a stale preview.
  const [syncPreview, setSyncPreview] = useState<{ kind: string; url: string; result: any } | null>(null);

  function clearSyncPreview() {
    setSyncResult(null);
    setSyncPreview(null);
  }

  async function loadPreview(e: FormEvent) {
    e.preventDefault();
    setMessage(null);
    try {
      const res = await api<any>("POST", "/api/migration/preview", { kind, baseUrl: url, apiKey: key });
      setPreview(res.items);
      setErrors(res.errors);
      setPicked(new Set(res.items.map((_: unknown, i: number) => i)));
      // The key is deliberately kept in this session's state (never stored): the monitoring sync
      // below needs the same key, and clearing it here used to force retyping it after every preview.
    } catch (x) {
      setMessage((x as Error).message);
    }
  }

  async function importSelected() {
    const items = (preview ?? []).filter((_, idx) => picked.has(idx)).map((item) => ({ type: item.type, tmdbId: item.tmdbId, title: item.title, monitored: item.monitored, legacyPath: item.path, legacySource: kind, seasons: item.seasons ?? [], episodes: item.episodes ?? [], episodesUnknown: item.episodesUnknown ?? false }));
    if (!confirm(`Copy ${items.length} catalog entr${items.length === 1 ? "y" : "ies"} into OneArr? This only writes to OneArr's database. ${kind} is not modified.`)) return;
    try {
      const res = await api<{ results: { title: string; status: string; message?: string }[] }>("POST", "/api/migration/import", {
        items, qualityProfileId: profileId === "" ? profiles?.[0]?.id : profileId, rootFolder: root,
      });
      setMessage(res.results.map((r) => `${r.title}: ${r.status}${r.message ? ` (${r.message})` : ""}`).join("\n"));
    } catch (x) {
      setMessage((x as Error).message);
    }
  }

  return (
    <>
      <h2>Migration Preview</h2>
      <p className="muted">Read-only: OneArr issues GET requests to the legacy API and never writes to it. Nothing is copied until you confirm. Existing files are not moved. After importing, map the legacy paths and run a library scan in Settings so existing files are recognised.</p>
      <form className="card row" onSubmit={loadPreview}>
        <select aria-label="Legacy application" value={kind} onChange={(e) => { setKind(e.target.value as "sonarr" | "radarr"); clearSyncPreview(); }}>
          <option value="radarr">Radarr (movies)</option>
          <option value="sonarr">Sonarr (series)</option>
        </select>
        <input aria-label="Legacy base URL" placeholder="http://radarr:7878" value={url} onChange={(e) => { setUrl(e.target.value); clearSyncPreview(); }} required style={{ minWidth: 240 }} />
        <input aria-label="Legacy API key" type="password" autoComplete="off" placeholder="API key (not stored)" value={key} onChange={(e) => { setKey(e.target.value); clearSyncPreview(); }} required />
        <button className="primary" type="submit">Load preview</button>
        <button type="button" onClick={async () => { setMessage(null); try { setTestResult(await api<ConnectionTestResult>("POST", "/api/connections/test", { service: "legacy", kind, baseUrl: url, apiKey: key })); } catch (x) { setMessage((x as Error).message); } }}>Test connection</button>
      </form>
      <ErrorLine message={message} />
      {testResult && <p className={testResult.ok ? "ok" : "error"}>{testResult.detail}{testResult.sampleTitles && testResult.sampleTitles.length > 0 ? ` Sample: ${testResult.sampleTitles.join(", ")}.` : ""}</p>}
      <div className="card">
        <h2>Monitoring sync for already-imported items</h2>
        <p className="muted">
          Use the same {kind} URL and key above. Preview first: it shows exactly which monitored flags would change and
          creates nothing. Apply is only enabled for the preview you just saw, for this same source — changing the source
          or key clears it, and the server re-checks the preview against the current source and database before writing,
          refusing if anything moved. Applying changes only monitored flags on items already in OneArr (matched by TMDb id);
          it never changes titles, quality profiles, root folders, or file state, and it never creates duplicates. Per-episode
          flags from {kind} are preserved inside a season; if episode data cannot be read for a series, its episodes are left
          alone and the series is listed below as unknown instead of being forced to its season flag.
        </p>
        <div className="row">
          <button onClick={async () => { setMessage(null); try { const res = await api<any>("POST", "/api/migration/monitoring-sync", { kind, baseUrl: url, apiKey: key, apply: false }); setSyncResult(res); setSyncPreview({ kind, url, result: res }); } catch (x) { setMessage((x as Error).message); } }}>Preview monitoring sync</button>
          <button className="primary" disabled={!syncPreview || syncPreview.kind !== kind || syncPreview.url !== url} onClick={async () => { if (!syncPreview) return; if (!confirm("Apply the monitoring sync preview you just saw? Only the monitored flags listed below will change.")) return; setMessage(null); try { const res = await api<any>("POST", "/api/migration/monitoring-sync", { kind, baseUrl: url, apiKey: key, apply: true, preview: { changes: syncPreview.result.changes, unmatchedTitles: syncPreview.result.unmatchedTitles, skippedNoTmdb: syncPreview.result.skippedNoTmdb, itemsChecked: syncPreview.result.itemsChecked, episodesUnknownTitles: syncPreview.result.episodesUnknownTitles ?? [] } }); setSyncResult(res); setSyncPreview(null); } catch (x) { setMessage(`${(x as Error).message} Run Preview again to see the fresh changes.`); setSyncPreview(null); } }}>Apply monitoring sync</button>
        </div>
        {!syncPreview && !syncResult?.applied && <p className="muted">Apply is disabled until you run Preview for the current source.</p>}
        {syncResult && (
          <div style={{ marginTop: ".75rem" }}>
            <p className="muted">{syncResult.applied ? "Applied" : "Preview only — nothing was written"}: {syncResult.itemsChecked} matched, {syncResult.changes.length} with changes, {syncResult.unmatchedTitles.length} legacy items not in OneArr, {syncResult.skippedNoTmdb} skipped (no TMDb id).</p>
            {syncResult.changes.length > 0 && (
              <ul>{syncResult.changes.map((c: any, i: number) => (
                <li key={i}>{c.title} ({c.type}): monitored {c.seriesMonitoredBefore ? "on" : "off"} → {c.seriesMonitoredAfter ? "on" : "off"} · {c.seasonsChanged} season(s) · {c.episodesChanged} episode(s){c.episodesUnknown ? " · episode data unavailable, episodes left alone" : ""}</li>
              ))}</ul>
            )}
            {syncResult.unmatchedTitles.length > 0 && <p className="muted">Not in OneArr (not created by sync): {syncResult.unmatchedTitles.join(", ")}</p>}
            {syncResult.episodesUnknownTitles?.length > 0 && <p className="error">Episode data could not be read for: {syncResult.episodesUnknownTitles.join(", ")}. Their episodes were not changed.</p>}
          </div>
        )}
      </div>
      {preview && (
        <div className="card">
          <p>{preview.length} readable item(s), {errors.length} error(s).</p>
          <div className="row" style={{ marginBottom: ".5rem" }}>
            <label>Quality profile
              <select value={profileId} onChange={(e) => setProfileId(e.target.value ? Number(e.target.value) : "")}>
                {profiles?.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </label>
            <label>Root folder
              <input value={root} onChange={(e) => setRoot(e.target.value)} placeholder={kind === "radarr" ? "D:\\Media\\Movies" : "D:\\Media\\TV"} />
            </label>
            <button className="primary" onClick={importSelected} disabled={picked.size === 0 || !root}>Import {picked.size} selected</button>
          </div>
          <div className="table-wrap"><table>
            <thead><tr><th /><th>Title</th><th>Year</th><th>TMDb</th><th>Path</th><th>Monitored</th></tr></thead>
            <tbody>
              {preview.map((i, idx) => (
                <tr key={idx}>
                  <td><input type="checkbox" checked={picked.has(idx)} onChange={(e) => { const n = new Set(picked); e.target.checked ? n.add(idx) : n.delete(idx); setPicked(n); }} /></td>
                  <td>{i.title}</td><td>{i.year ?? ""}</td><td>{i.tmdbId ?? <span className="error">none</span>}</td><td className="muted">{i.path}</td><td>{i.monitored ? "yes" : "no"}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
          {errors.length > 0 && <div style={{ marginTop: ".75rem" }}><h2>Per-item errors</h2><ul>{errors.map((e, i) => <li key={i}>{e.title}: {e.error}</li>)}</ul></div>}
        </div>
      )}
    </>
  );
}
