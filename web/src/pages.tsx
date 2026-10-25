import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, ApiError, type EpisodeRow, type Job, type MediaItem, type MetadataResult, type QualityProfile, type ReleaseResult } from "./api";

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

function MediaPage({ kind }: { kind: "movie" | "series" }) {
  const label = kind === "movie" ? "Movies" : "Series";
  const { data: items, error, reload } = useLoad<MediaItem[]>(`/api/media?type=${kind}`, [kind]);
  const { data: profiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const [openId, setOpenId] = useState<number | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  async function toggle(item: MediaItem) {
    await api("PATCH", `/api/media/${item.id}`, { monitored: !item.monitored }).catch((e: Error) => setMessage(e.message));
    reload();
  }
  async function remove(item: MediaItem) {
    if (!confirm(`Remove "${item.title}" from OneArr? Files and torrents are not touched.`)) return;
    await api("DELETE", `/api/media/${item.id}`).catch((e: Error) => setMessage(e.message));
    reload();
  }

  return (
    <>
      <h2>{label}</h2>
      <AddMedia kind={kind} profiles={profiles ?? []} onAdded={reload} />
      <ErrorLine message={error ?? message} />
      <div className="card table-wrap">
        {items && items.length === 0 && <p className="muted">Nothing here yet. Search TMDb above to add a {kind === "movie" ? "movie" : "series"}.</p>}
        {items && items.length > 0 && (
          <table>
            <thead><tr><th>Title</th><th>Year</th><th>Monitored</th><th>Profile</th><th>File</th><th /></tr></thead>
            <tbody>
              {items.map((item) => (
                <tr key={item.id}>
                  <td>
                    {item.title}
                    {kind === "series" && openId === item.id && <EpisodeList mediaId={item.id} />}
                  </td>
                  <td>{item.year ?? ""}</td>
                  <td><button className="small" onClick={() => toggle(item)}>{item.monitored ? "Yes" : "No"}</button></td>
                  <td>{profiles?.find((p) => p.id === item.qualityProfileId)?.name ?? item.qualityProfileId}</td>
                  <td>{item.hasFile ? <span className="badge ok">have</span> : <span className="badge">missing</span>}</td>
                  <td className="row">
                    {kind === "movie" && <button className="small" onClick={() => setOpenId(openId === item.id ? null : item.id)}>Releases</button>}
                    {kind === "series" && <button className="small" onClick={() => setOpenId(openId === item.id ? null : item.id)}>Episodes</button>}
                    <button className="small danger" onClick={() => remove(item)}>Remove</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      {kind === "movie" && openId !== null && <ReleasePanel mediaId={openId} episodeId={null} onClose={() => setOpenId(null)} />}
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
    if (!root.startsWith("/")) return setMessage("Enter an absolute root folder, e.g. /data/media/movies");
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
          <input aria-label="Root folder" value={root} onChange={(e) => setRoot(e.target.value)} placeholder="/data/media/movies" />
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

function EpisodeList({ mediaId }: { mediaId: number }) {
  const { data, error } = useLoad<{ episodes: EpisodeRow[] }>(`/api/media/${mediaId}`, [mediaId]);
  const [episodeId, setEpisodeId] = useState<number | null>(null);
  const episodes = (data as unknown as { episodes?: EpisodeRow[] } | null)?.episodes ?? [];
  return (
    <div style={{ marginTop: ".5rem" }}>
      <ErrorLine message={error} />
      {episodes.length === 0 && !error && <p className="muted">No episodes loaded.</p>}
      <table>
        <tbody>
          {episodes.sort((a, b) => a.seasonNumber - b.seasonNumber || a.episodeNumber - b.episodeNumber).map((e) => (
            <tr key={e.id}>
              <td>S{String(e.seasonNumber).padStart(2, "0")}E{String(e.episodeNumber).padStart(2, "0")}</td>
              <td>{e.title}</td>
              <td>{e.airDate ?? ""}</td>
              <td>{e.hasFile ? <span className="badge ok">have</span> : <span className="badge">missing</span>}</td>
              <td><button className="small" onClick={() => setEpisodeId(episodeId === e.id ? null : e.id)}>Releases</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      {episodeId !== null && <ReleasePanel mediaId={mediaId} episodeId={episodeId} onClose={() => setEpisodeId(null)} />}
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
  const { data, error } = useLoad<any>("/api/wanted");
  return (
    <>
      <h2>Wanted</h2>
      <ErrorLine message={error} />
      {data && (
        <>
          <div className="card">
            <h2>Movies without a file ({data.movies.length})</h2>
            {data.movies.length === 0 ? <p className="muted">None.</p> : <ul>{data.movies.map((m: MediaItem) => <li key={m.id}>{m.title} {m.year ? `(${m.year})` : ""}</li>)}</ul>}
          </div>
          <div className="card table-wrap">
            <h2>Aired episodes without a file ({data.episodes.length})</h2>
            {data.episodes.length === 0 ? <p className="muted">None.</p> : (
              <table><thead><tr><th>Series</th><th>Episode</th><th>Title</th><th>Aired</th></tr></thead><tbody>
                {data.episodes.map((e: any) => (
                  <tr key={e.episodeId}><td>{e.seriesTitle}</td><td>S{String(e.seasonNumber).padStart(2, "0")}E{String(e.episodeNumber).padStart(2, "0")}</td><td>{e.episodeTitle}</td><td>{e.airDate}</td></tr>
                ))}
              </tbody></table>
            )}
          </div>
        </>
      )}
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
            <thead><tr><th>Release</th><th>Status</th><th>Client state</th><th>Progress</th><th>Category</th><th>Hash</th></tr></thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.id}>
                  <td>{j.releaseTitle}{j.error && <div className="error">{j.error}</div>}</td>
                  <td>{j.status}</td>
                  <td>{j.clientState || "—"}</td>
                  <td>{Math.round(j.progress * 100)}%</td>
                  <td>{j.category}</td>
                  <td className="muted">{j.hash ? j.hash.slice(0, 10) : "—"}</td>
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
      {data && <GeneralForm data={data} onSave={save} />}

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
  return (
    <form className="card" onSubmit={(e) => { e.preventDefault(); onSave({ tmdbApiKey: tmdb || undefined, torrentRoot, mediaRoot, dryRunImport: dry }); setTmdb(""); }}>
      <h2>General</h2>
      <label>TMDb API key {data.tmdbConfigured ? <span className="ok">configured</span> : <span className="error">not set</span>}
        <input type="password" autoComplete="off" value={tmdb} onChange={(e) => setTmdb(e.target.value)} placeholder={data.tmdbConfigured ? "Leave blank to keep the stored key" : "Paste your TMDb v3 key or v4 read token"} />
      </label>
      <label>Torrent download root (absolute)
        <input value={torrentRoot} onChange={(e) => setTorrentRoot(e.target.value)} placeholder="/data/torrents" />
      </label>
      <label>Media root (absolute, imports go here)
        <input value={mediaRoot} onChange={(e) => setMediaRoot(e.target.value)} placeholder="/data/media" />
      </label>
      <label className="inline"><input type="checkbox" checked={dry} onChange={(e) => setDry(e.target.checked)} /> Imports are dry-run only (recommended until verified)</label>
      <p className="muted">Automatic grabbing is not implemented in this build; all grabs are manual.</p>
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
  const [errors, setErrors] = useState<{ title: string; error: string }[]>([]);
  const [picked, setPicked] = useState<Set<number>>(new Set());
  const { data: profiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const [profileId, setProfileId] = useState<number | "">("");
  const [root, setRoot] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  async function loadPreview(e: FormEvent) {
    e.preventDefault();
    setMessage(null);
    try {
      const res = await api<any>("POST", "/api/migration/preview", { kind, baseUrl: url, apiKey: key });
      setPreview(res.items);
      setErrors(res.errors);
      setPicked(new Set(res.items.map((_: unknown, i: number) => i)));
      setKey("");
    } catch (x) {
      setMessage((x as Error).message);
    }
  }

  async function importSelected() {
    const items = (preview ?? []).filter((_, idx) => picked.has(idx)).map((item) => ({ type: item.type, tmdbId: item.tmdbId, title: item.title, monitored: item.monitored }));
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
      <p className="muted">Read-only: OneArr issues GET requests to the legacy API and never writes to it. Nothing is copied until you confirm.</p>
      <form className="card row" onSubmit={loadPreview}>
        <select aria-label="Legacy application" value={kind} onChange={(e) => setKind(e.target.value as "sonarr" | "radarr")}>
          <option value="radarr">Radarr (movies)</option>
          <option value="sonarr">Sonarr (series)</option>
        </select>
        <input aria-label="Legacy base URL" placeholder="http://radarr:7878" value={url} onChange={(e) => setUrl(e.target.value)} required style={{ minWidth: 240 }} />
        <input aria-label="Legacy API key" type="password" autoComplete="off" placeholder="API key (not stored)" value={key} onChange={(e) => setKey(e.target.value)} required />
        <button className="primary" type="submit">Load preview</button>
      </form>
      <ErrorLine message={message} />
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
              <input value={root} onChange={(e) => setRoot(e.target.value)} placeholder={kind === "radarr" ? "/data/media/movies" : "/data/media/tv"} />
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
