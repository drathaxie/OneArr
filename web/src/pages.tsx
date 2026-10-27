import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError, formatBytes, posterUrl, type Job, type MediaDetail, type MediaFile, type MediaItem, type MetadataResult, type QualityProfile, type ReleaseResult, type SettingsData } from "./api";
import { ErrorLine, useLoad } from "./common";

export { Settings } from "./settings";
export { Migration } from "./migration";

function availabilityClass(status: string | null | undefined): string {
  if (status === "Complete" || status === "Available") return "badge ok";
  if (status === "Missing") return "badge bad";
  if (status === "Partial") return "badge warn";
  return "badge";
}

function seriesAvailability(item: MediaItem): string {
  return item.availability ?? "Unknown";
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
  const [filter, setFilter] = useState<string>("all");
  const [showAdd, setShowAdd] = useState(false);

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

  const availabilityRank = (item: MediaItem) => {
    if (kind === "movie") return item.hasFile ? 0 : 1;
    return { Complete: 0, Partial: 1, Missing: 2, Unknown: 3 }[seriesAvailability(item)] ?? 3;
  };
  const filtered = (items ?? []).filter((item) => {
    if (query && !item.title.toLowerCase().includes(query.toLowerCase())) return false;
    if (filter === "available" && !item.hasFile) return false;
    if (filter === "missing" && item.hasFile) return false;
    if (filter === "monitored" && !item.monitored) return false;
    if (filter === "unmonitored" && item.monitored) return false;
    if (kind === "series" && ["Complete", "Partial", "Missing", "Unknown"].includes(filter) && seriesAvailability(item) !== filter) return false;
    return true;
  }).sort((a, b) => {
    if (sort === "year") return (b.year ?? 0) - (a.year ?? 0) || a.title.localeCompare(b.title);
    if (sort === "status") return availabilityRank(a) - availabilityRank(b) || a.title.localeCompare(b.title);
    return a.title.localeCompare(b.title);
  });

  if (detailId !== null) {
    return <MediaDetail mediaId={detailId} profiles={profiles ?? []} onBack={() => { setDetailId(null); reload(); }} />;
  }

  return (
    <>
      <h2>{label}</h2>
      <div className="row" style={{ marginBottom: "1rem" }}>
        <button className="primary" onClick={() => setShowAdd(true)}>Add {kind === "movie" ? "Movie" : "Series"}</button>
        <span className="muted">Root folders, profiles, and bulk changes live in Settings → Media Management.</span>
      </div>
      {showAdd && <AddMediaDialog kind={kind} profiles={profiles ?? []} onAdded={() => { setShowAdd(false); reload(); }} onClose={() => setShowAdd(false)} />}
      <ErrorLine message={error ?? message} />
      <div className="card">
        <div className="row">
          <input aria-label={`Search ${label}`} placeholder="Search titles…" value={query} onChange={(e) => setQuery(e.target.value)} style={{ minWidth: 200 }} />
          <select aria-label="Sort" value={sort} onChange={(e) => setSort(e.target.value as "title" | "year" | "status")}>
            <option value="title">Sort: title</option>
            <option value="year">Sort: year (newest)</option>
            <option value="status">Sort: available first</option>
          </select>
          <select aria-label="Filter" value={filter} onChange={(e) => setFilter(e.target.value)}>
            <option value="all">All</option>
            {kind === "movie" && (<><option value="available">Available</option><option value="missing">Missing</option></>)}
            {kind === "series" && (<><option value="Complete">Complete</option><option value="Partial">Partial</option><option value="Missing">Missing</option><option value="Unknown">Unknown</option></>)}
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
      {items && items.length === 0 && <p className="muted">Nothing here yet. Use Add {kind === "movie" ? "Movie" : "Series"} above to search TMDb.</p>}
      {items && items.length > 0 && filtered.length === 0 && <p className="muted">No titles match the current search and filter.</p>}
      {view === "grid" && filtered.length > 0 && (
        <div className="poster-grid">
          {filtered.map((item) => (
            <article key={item.id} className="poster-card">
              <button className="poster-button" onClick={() => setDetailId(item.id)} aria-label={`Open ${item.title}`}>
                <Poster item={item} />
              </button>
              <div className="poster-info">
                <strong className="poster-title" title={item.title} onClick={() => setDetailId(item.id)} style={{ cursor: "pointer" }}>{item.title}</strong>
                <div className="muted">{item.year ?? ""}</div>
                <div className="row" style={{ marginTop: ".25rem" }}>
                  {kind === "movie"
                    ? <span className={item.hasFile ? "badge ok" : "badge bad"}>{item.hasFile ? "Available" : "Missing"}</span>
                    : <span className={availabilityClass(seriesAvailability(item))}>{seriesAvailability(item)}</span>}
                  <span className="badge">{item.monitored ? "Monitored" : "Unmonitored"}</span>
                </div>
                {kind === "series" && item.applicableEpisodes != null && item.applicableEpisodes > 0 && (
                  <div className="muted" style={{ fontSize: ".85rem" }}>{item.availableEpisodes ?? 0} / {item.applicableEpisodes} monitored aired episodes available</div>
                )}
                {kind === "series" && (item.applicableEpisodes == null || item.applicableEpisodes === 0) && (
                  <div className="muted" style={{ fontSize: ".85rem" }}>Episode data insufficient to classify</div>
                )}
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
                  <td>{kind === "movie"
                    ? (item.hasFile ? <span className="badge ok">have</span> : <span className="badge">missing</span>)
                    : (<span className={availabilityClass(seriesAvailability(item))}>{seriesAvailability(item)}{item.applicableEpisodes != null && item.applicableEpisodes > 0 ? ` ${item.availableEpisodes ?? 0}/${item.applicableEpisodes}` : ""}</span>)}</td>
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
  const wantedEpisodes = data.seasons.reduce((n, s) => n + s.wantedCount, 0);
  // Availability comes from the server: monitored, aired episodes only. A series with one downloaded
  // episode is never Complete just because its series-level HasFile flag is true, and unaired or
  // unmonitored episodes never count as missing.
  const fileStatus = data.availability ?? (isSeries ? "Unknown" : data.hasFile ? "Available" : "Missing");

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
            <span className={availabilityClass(fileStatus)}>{fileStatus}</span>
            <span className="badge">{data.monitored ? "Monitored" : "Unmonitored"}</span>
            <span className="badge">{profiles.find((p) => p.id === data.qualityProfileId)?.name ?? data.qualityProfileId}</span>
            {isSeries && <span className="badge">Wanted episodes: {wantedEpisodes}</span>}
          </div>
          {isSeries && data.applicableEpisodes != null && data.applicableEpisodes > 0 && (
            <p className="muted">{data.availableEpisodes ?? 0} / {data.applicableEpisodes} monitored aired episodes available</p>
          )}
          {isSeries && (data.applicableEpisodes == null || data.applicableEpisodes === 0) && (
            <p className="muted">Episode data insufficient to classify availability.</p>
          )}
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

function AddMediaDialog({ kind, profiles, onAdded, onClose }: { kind: "movie" | "series"; profiles: QualityProfile[]; onAdded: () => void; onClose: () => void }) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<MetadataResult[]>([]);
  const [selected, setSelected] = useState<MetadataResult | null>(null);
  const [settings, setSettings] = useState<SettingsData | null>(null);
  const [profileId, setProfileId] = useState<number | "">("");
  const [root, setRoot] = useState("");
  const [rootTouched, setRootTouched] = useState(false);
  // Whether the server had a saved default for this type. When false, the root starts empty on
  // purpose: the dialog must never silently prefill the other type's folder (or the old shared
  // media root) for this title. The user types a folder, or sets a per-type default in Settings.
  const [hasDefaultRoot, setHasDefaultRoot] = useState(true);
  const [monitorScope, setMonitorScope] = useState<"all" | "first" | "none">("all");
  const [monitored, setMonitored] = useState(true);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  // Defaults load when the dialog opens, from the server, per media type — and only per media type.
  // There is no fallback to the shared media root or to any previously used folder: either of those
  // could silently put a series in the Movies folder (or a movie in the TV folder). With no saved
  // default for this type, the root starts empty and the Add button stays disabled until the user
  // types one. The legacy localStorage root is no longer read at all.
  useEffect(() => {
    api<SettingsData>("GET", "/api/settings")
      .then((s) => {
        setSettings(s);
        const perTypeRoot = kind === "movie" ? s.defaultMovieRoot : s.defaultSeriesRoot;
        const perTypeProfile = kind === "movie" ? s.defaultMovieProfileId : s.defaultSeriesProfileId;
        setHasDefaultRoot(!!perTypeRoot);
        setRoot(perTypeRoot ?? "");
        if (perTypeProfile != null) setProfileId(perTypeProfile);
      })
      .catch(() => {
        setMessage("Could not load saved defaults; check Settings → Media Management. You can still type a root folder manually.");
        setHasDefaultRoot(false);
        setSettings({ tmdbConfigured: false, dryRunImport: true, torrentRoot: null, mediaRoot: null, automaticGrabbing: false, defaultMovieRoot: null, defaultSeriesRoot: null, defaultMovieProfileId: null, defaultSeriesProfileId: null });
      });
  }, [kind]);

  useEffect(() => {
    if (profileId === "" && profiles.length > 0) setProfileId(profiles[0].id);
  }, [profiles, profileId]);

  async function search(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      setResults(await api<MetadataResult[]>("GET", `/api/metadata/search?type=${kind}&q=${encodeURIComponent(query)}`));
      setSelected(null);
    } catch (err) {
      setMessage((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function add() {
    if (!selected) return;
    const pid = profileId === "" ? profiles[0]?.id : profileId;
    if (!pid) return setMessage("Create a quality profile in Settings first");
    if (!/^([A-Za-z]:[\\]|\\\\|\/)/.test(root)) return setMessage("Enter a folder path, e.g. D:\\Media\\Movies");
    setBusy(true);
    try {
      const body: Record<string, unknown> = {
        type: kind, providerId: selected.providerId, qualityProfileId: pid, rootFolder: root,
        monitored: kind === "movie" ? monitored : monitorScope !== "none",
        seasons: kind === "series" && monitorScope === "first" ? [1] : null,
        allSeasons: kind === "series" ? monitorScope === "all" : true,
      };
      const res = await api<{ created: boolean; item: MediaItem }>("POST", "/api/media", body);
      setMessage(res.created ? `Added ${selected.title}` : `${selected.title} is already in OneArr`);
      onAdded();
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : (err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const effectiveProfile = profiles.find((p) => p.id === profileId) ?? profiles[0];

  return (
    <div className="dialog-backdrop" role="presentation" onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="dialog card" role="dialog" aria-modal="true" aria-label={`Add ${kind === "movie" ? "Movie" : "Series"}`}>
        <div className="row spread">
          <h2>Add {kind === "movie" ? "Movie" : "Series"}</h2>
          <button onClick={onClose}>Close</button>
        </div>
        <form className="row" onSubmit={search}>
          <input aria-label="Search TMDb" placeholder={`Search TMDb for a ${kind === "movie" ? "movie" : "series"}`} value={query} onChange={(e) => setQuery(e.target.value)} style={{ flex: 1, minWidth: 220 }} />
          <button className="primary" type="submit" disabled={busy || !query.trim()}>Search</button>
        </form>
        {results.length > 0 && (
          <ul className="results">
            {results.slice(0, 15).map((r) => (
              <li key={r.providerId} className="row spread" style={{ padding: ".3rem 0" }}>
                <span>{r.title}{r.year ? ` (${r.year})` : ""} <span className="muted">#{r.providerId}</span></span>
                <button className="small primary" disabled={selected?.providerId === r.providerId} onClick={() => setSelected(r)}>{selected?.providerId === r.providerId ? "Selected" : "Select"}</button>
              </li>
            ))}
          </ul>
        )}
        {settings !== null && !hasDefaultRoot && (
          <p className="muted">No saved {kind === "movie" ? "movie" : "series"} default root folder. Set one in Settings → Media Management → Default root folders, or type a folder here.</p>
        )}
        <div className="row" style={{ marginTop: ".5rem" }}>
              <label style={{ minWidth: 200 }}>Root folder
                <input aria-label="Root folder" value={root} onChange={(e) => { setRoot(e.target.value); setRootTouched(true); }} placeholder={kind === "movie" ? "D:\\Media\\Movies" : "D:\\Media\\TV"} />
              </label>
              <label>Quality profile
                <select aria-label="Quality profile" value={profileId} onChange={(e) => setProfileId(e.target.value ? Number(e.target.value) : "")}>
                  {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                </select>
              </label>
              {kind === "movie" ? (
                <label className="inline"><input type="checkbox" checked={monitored} onChange={(e) => setMonitored(e.target.checked)} /> Monitor</label>
              ) : (
                <label>Monitor
                  <select aria-label="Monitor scope" value={monitorScope} onChange={(e) => setMonitorScope(e.target.value as "all" | "first" | "none")}>
                    <option value="all">All seasons</option>
                    <option value="first">First season only</option>
                    <option value="none">None (unmonitored)</option>
                  </select>
                </label>
              )}
        </div>
        {selected && (
          <>
            <div className="card" style={{ marginTop: ".75rem" }}>
              <p className="muted">Confirm before adding:</p>
              <p><strong>{selected.title}{selected.year ? ` (${selected.year})` : ""}</strong> → <code>{root || "(no root folder)"}</code> · profile {effectiveProfile?.name ?? "—"} · {kind === "movie" ? (monitored ? "monitored" : "unmonitored") : monitorScope === "all" ? "all seasons monitored" : monitorScope === "first" ? "first season monitored" : "unmonitored"}</p>
              <p className="muted">Adding only writes to OneArr's database. It does not move files or change any title already in OneArr.{rootTouched || !hasDefaultRoot ? "" : " The root folder came from your saved default for this media type."}</p>
              <button className="primary" disabled={busy || !root.trim()} onClick={add}>Add {selected.title}</button>
            </div>
          </>
        )}
        <ErrorLine message={message} />
        {!selected && settings === null && <p className="muted">Loading saved defaults…</p>}
      </div>
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
