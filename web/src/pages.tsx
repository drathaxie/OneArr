import { useEffect, useState, type FormEvent } from "react";
import { useRef } from "react";
import { api, ApiError, formatBytes, posterUrl, type DownloadClient, type HistoryEntry, type ImportPreview, type IndexerSearchOutcome, type MediaDetail, type MediaFile, type MediaItem, type MetadataResult, type Problem, type QualityProfile, type QueueData, type ReleaseResult, type SettingsData } from "./api";
import { availabilityClass, availabilityStamp, ConfirmDialog, ErrorLine, primaryStatus, useLoad } from "./common";
import { InspectorPanel, OpsStrip, type CommandNavigate } from "./command-center";

export { Settings } from "./settings";
export { Migration } from "./migration";

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
            <div className="table-wrap"><table><thead><tr><th>Job</th><th>Started</th><th>Result</th></tr></thead><tbody>
              {data.recentJobs.map((j: any, i: number) => (
                <tr key={i}><td>{j.name}</td><td>{new Date(j.startedAt).toLocaleString()}</td>
                  <td><span className={j.success ? "badge ok" : "badge bad"}>{j.success ? "ok" : "failed"}</span> {j.message}</td></tr>
              ))}
            </tbody></table></div>
          )}
        </div>
      )}
    </>
  );
}

function Stat({ label, value }: { label: string; value: number }) {
  return <div className="card"><div className="muted">{label}</div><div className="stat">{value}</div></div>;
}

export function Movies({ onNavigate }: { onNavigate?: CommandNavigate }) {
  return <MediaPage kind="movie" onNavigate={onNavigate} />;
}

export function Series({ onNavigate }: { onNavigate?: CommandNavigate }) {
  return <MediaPage kind="series" onNavigate={onNavigate} />;
}

function Poster({ item, size }: { item: MediaItem; size?: "card" | "thumb" }) {
  const url = posterUrl(item.posterPath);
  if (!url) return <div className={size === "thumb" ? "poster-thumb poster-fallback" : "poster poster-fallback"}><span>{item.title}</span></div>;
  return <img className={size === "thumb" ? "poster-thumb" : "poster"} src={url} alt={`${item.title} poster`} loading="lazy" />;
}

function MediaPage({ kind, onNavigate }: { kind: "movie" | "series"; onNavigate?: CommandNavigate }) {
  const label = kind === "movie" ? "Movies" : "Series";
  const { data: items, error, reload } = useLoad<MediaItem[]>(`/api/media?type=${kind}`, [kind]);
  const { data: profiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const [filesId, setFilesId] = useState<number | null>(null);
  const [detailId, setDetailId] = useState<number | null>(null);
  const [searchId, setSearchId] = useState<number | null>(null);
  const [inspectorId, setInspectorId] = useState<number | null>(null);
  const [menuId, setMenuId] = useState<number | null>(null);
  const [pendingRemove, setPendingRemove] = useState<MediaItem | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [view, setView] = useState<"grid" | "list">(() => (localStorage.getItem("onearr.view") as "grid" | "list") || "grid");
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<"title" | "year" | "status">("title");
  const [filter, setFilter] = useState<string>("all");
  const [showAdd, setShowAdd] = useState(false);
  // The poster button that opened the inspector, so closing can return focus to it.
  const openerRef = useRef<HTMLButtonElement | null>(null);
  const releaseOpenerRef = useRef<HTMLButtonElement | null>(null);

  function setViewMode(v: "grid" | "list") {
    setView(v);
    try { localStorage.setItem("onearr.view", v); } catch { /* view preference is best-effort */ }
  }

  async function toggle(item: MediaItem) {
    await api("PATCH", `/api/media/${item.id}`, { monitored: !item.monitored }).catch((e: Error) => setMessage(e.message));
    reload();
  }
  // Removal is never immediate: pendingRemove renders a confirmation dialog, and the
  // DELETE only runs when the user clicks Remove there. Cancel, Escape, and backdrop
  // clicks all leave the title untouched. Files and torrents are never touched by this
  // endpoint — that wording is part of the dialog itself.
  async function confirmRemove() {
    if (!pendingRemove) return;
    const item = pendingRemove;
    setPendingRemove(null);
    if (inspectorId === item.id) closeInspector();
    await api("DELETE", `/api/media/${item.id}`).catch((e: Error) => setMessage(e.message));
    reload();
  }
  function openInspector(item: MediaItem, opener: HTMLButtonElement | null) {
    openerRef.current = opener;
    setInspectorId(item.id);
  }
  function openReleaseSearch(id: number, opener: HTMLButtonElement) {
    releaseOpenerRef.current = opener;
    setSearchId(id);
  }

  function closeReleaseSearch() {
    const opener = releaseOpenerRef.current;
    setSearchId(null);
    // The menu item may have unmounted when its menu closed. Prefer the
    // original button when it still exists, otherwise restore focus to the
    // selected poster/dossier rather than losing focus to the document.
    setTimeout(() => {
      if (opener?.isConnected) { opener.focus(); return; }
      const dossierAction = document.querySelector<HTMLButtonElement>(".cc-dossier-actions > button.primary");
      const searchPoster = document.querySelector<HTMLButtonElement>(`[data-poster-button="${searchId}"]`);
      const selectedPoster = inspectorId === null ? null
        : document.querySelector<HTMLButtonElement>(`[data-poster-button="${inspectorId}"]`);
      (dossierAction ?? searchPoster ?? selectedPoster ?? openerRef.current)?.focus();
    }, 0);
  }

  function closeInspector() {
    // Focus management, part 2: focus returns to the poster of the title the
    // inspector was showing when it closed (stepping changes that title, so the
    // original opener is only a fallback), deferred a tick so the panel has
    // unmounted before focus moves.
    const current = inspectorId === null ? null : document.querySelector<HTMLButtonElement>(`[data-poster-button="${inspectorId}"]`);
    const opener = current ?? openerRef.current;
    setInspectorId(null);
    setTimeout(() => { if (opener && document.contains(opener)) opener.focus(); }, 0);
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

  // Inspector stepping follows the visible, filtered, sorted order — the same order
  // the grid shows — not the raw list order.
  const inspectorIndex = inspectorId === null ? -1 : filtered.findIndex((i) => i.id === inspectorId);
  const inspectorItem = inspectorIndex >= 0 ? filtered[inspectorIndex] : null;

  // Dossier/grid synchronization on filtering: when the user changes the search
  // text or the filter and the dossier's title leaves the visible list, the
  // selection itself is cleared (not just hidden) so it cannot resurrect when
  // the filter is cleared. Focus is deliberately not moved — it stays in the
  // search input or filter select the user is typing in. Data reloads (a PATCH
  // from the dossier, a removal) do not run this effect, so acting on a title
  // from inside its own dossier never yanks the dossier away mid-action.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (inspectorId !== null && items && !filtered.some((i) => i.id === inspectorId)) setInspectorId(null);
  }, [query, filter]);
  function stepInspector(dir: -1 | 1) {
    const next = inspectorIndex + dir;
    if (next >= 0 && next < filtered.length) setInspectorId(filtered[next].id);
  }

  // Keyboard shortcuts for the inspector, with hard guards so they never interfere
  // with text inputs, forms, menus, or the confirmation dialog:
  // - if focus is in an input, textarea, select, or contenteditable element, skip all
  // - if a poster menu is open, Escape closes the menu (and arrows do nothing)
  // - if the remove dialog is open, it owns Escape (capture-phase handler in the dialog)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (inspectorId === null || pendingRemove !== null || searchId !== null) return;
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT" || target.isContentEditable)) return;
      if (e.key === "Escape" && menuId === null) {
        e.preventDefault();
        closeInspector();
        return;
      }
      if (menuId !== null) {
        if (e.key === "Escape") { e.preventDefault(); setMenuId(null); }
        return;
      }
      if (e.key === "ArrowLeft") { e.preventDefault(); stepInspector(-1); }
      if (e.key === "ArrowRight") { e.preventDefault(); stepInspector(1); }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
    // Re-bind when the inspector target or menu state changes so the guards above
    // always see the current state; the handler itself is cheap.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [inspectorId, menuId, pendingRemove, searchId, filtered]);

  // A poster menu closes when the user clicks anywhere outside it (its own button
  // and items are the only inside targets).
  useEffect(() => {
    if (menuId === null) return;
    const onClick = (e: MouseEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && !target.closest(".cc-menu") && !target.closest(".cc-card-menu")) setMenuId(null);
    };
    document.addEventListener("click", onClick);
    return () => document.removeEventListener("click", onClick);
  }, [menuId]);

  if (detailId !== null) {
    return <div className="dialog-backdrop series-workspace-backdrop"><section role="dialog" aria-modal="true" aria-label="Media manager" className="series-workspace-dialog"><MediaDetail mediaId={detailId} profiles={profiles ?? []} onBack={() => { setDetailId(null); reload(); }} /></section></div>;
  }

  return (
    <>
      <div className="cc-page-head">
        <h2 className="cc-page-title serif">{label}</h2>
        <p className="cc-inventory mono">{(items ?? []).length} TITLES IN LIBRARY · {view === "grid" ? "POSTER" : "LIST"} VIEW</p>
      </div>
      <OpsStrip onNavigate={onNavigate} />
      <div className="cc-command-bar">
        <label className="cc-search">⌕ <input aria-label={`Search ${label}`} placeholder="Search titles, years, profiles…" value={query} onChange={(e) => setQuery(e.target.value)} /></label>
        <label className="cc-ctl"><span className="mono">Sort</span>
          <select aria-label="Sort" value={sort} onChange={(e) => setSort(e.target.value as "title" | "year" | "status")}>
            <option value="title">Title</option>
            <option value="year">Year (newest)</option>
            <option value="status">Available first</option>
          </select>
        </label>
        <label className="cc-ctl"><span className="mono">Filter</span>
          <select aria-label="Filter" value={filter} onChange={(e) => setFilter(e.target.value)}>
            <option value="all">All</option>
            {kind === "movie" && (<><option value="available">Available</option><option value="missing">Missing</option></>)}
            {kind === "series" && (<><option value="Complete">Complete</option><option value="Partial">Partial</option><option value="Missing">Missing</option><option value="Unknown">Unknown</option></>)}
            <option value="monitored">Monitored</option>
            <option value="unmonitored">Unmonitored</option>
          </select>
        </label>
        <span className="cc-result-count mono">Showing {filtered.length} of {(items ?? []).length}.</span>
        <div className="cc-command-right">
          <div className="cc-seg" role="group" aria-label="View">
            <button className={view === "grid" ? "active" : ""} onClick={() => setViewMode("grid")}>Posters</button>
            <button className={view === "list" ? "active" : ""} onClick={() => setViewMode("list")}>List</button>
          </div>
          <button className="primary" onClick={() => setShowAdd(true)}>Add {kind === "movie" ? "Movie" : "Series"}</button>
        </div>
      </div>
      {showAdd && <AddMediaDialog kind={kind} profiles={profiles ?? []} onAdded={() => { setShowAdd(false); reload(); }} onClose={() => setShowAdd(false)} />}
      <ErrorLine message={error ?? message} />
      {items && items.length === 0 && <p className="muted">Nothing here yet. Use Add {kind === "movie" ? "Movie" : "Series"} above to search TMDb.</p>}
      {items && items.length > 0 && filtered.length === 0 && <p className="muted">No titles match the current search and filter.</p>}
      {view === "grid" && filtered.length > 0 && (
        <div className={inspectorItem ? "cc-workspace open" : "cc-workspace"}>
          <div className="cc-grid">
            {filtered.map((item) => {
              const status = primaryStatus(item, kind);
              const menuOpen = menuId === item.id;
              // Card status, refined Concept A: availability as an outlined mono
              // stamp, monitoring as separate mono text beside it. Exactly one
              // monitoring indicator per card (the note), and the profile is
              // labelled as a profile — never presented as a file resolution.
              // The stamp rule lives in common.availabilityStamp, shared with
              // the dossier, so the two can never drift apart again.
              const stamp = availabilityStamp(item, kind);
              const stampText = stamp.text;
              const stampCls = stamp.cls;
              return (
              <article key={item.id} className={inspectorId === item.id ? "cc-card selected" : "cc-card"}>
                <div className="cc-poster-wrap">
                  <button
                    data-poster-button={item.id}
                    className="cc-poster-button"
                    onClick={(e) => openInspector(item, e.currentTarget)}
                    aria-label={`Open ${item.title}`}
                  >
                    <Poster item={item} />
                  </button>
                </div>
                <div className="cc-card-info">
                  <div className="cc-card-head">
                    <button className="cc-title" title={item.title} onClick={() => openInspector(item, document.querySelector<HTMLButtonElement>(`[data-poster-button="${item.id}"]`))}>{item.title}</button>
                    <button
                      className="cc-card-menu"
                      aria-label={`Actions for ${item.title}`}
                      aria-haspopup="menu"
                      aria-expanded={menuOpen}
                      onClick={() => setMenuId(menuOpen ? null : item.id)}
                    >···</button>
                    {menuOpen && (
                      <div className="cc-menu" role="menu">
                        <button role="menuitem" onClick={() => { setMenuId(null); setDetailId(item.id); }}>Details</button>
                        <button role="menuitem" onClick={() => { setMenuId(null); setFilesId(item.id); }}>Files</button>
                        <button role="menuitem" onClick={(e) => { setMenuId(null); openReleaseSearch(item.id, e.currentTarget); }}>Search releases</button>
                        <button role="menuitem" onClick={() => { setMenuId(null); toggle(item); }}>{item.monitored ? "Unmonitor" : "Monitor"}</button>
                        <button role="menuitem" className="danger" onClick={() => { setMenuId(null); setPendingRemove(item); }}>Remove…</button>
                      </div>
                    )}
                  </div>
                  <div className="muted cc-meta mono">{item.year ?? ""} · Profile {profiles?.find((p) => p.id === item.qualityProfileId)?.name ?? item.qualityProfileId}</div>
                  <div className="cc-status-row"><span className={stampCls}>{stampText}</span><span className="cc-mon-note mono">{item.monitored ? "MONITORED" : "UNMONITORED"}</span></div>
                  {item.monitored && kind === "series" && item.applicableEpisodes != null && item.applicableEpisodes > 0 && (
                    <div className="muted cc-meta">{item.availableEpisodes ?? 0} / {item.applicableEpisodes} monitored aired episodes available</div>
                  )}
                  {item.monitored && kind === "series" && (item.applicableEpisodes == null || item.applicableEpisodes === 0) && (
                    <div className="muted cc-meta">Episode data insufficient to classify</div>
                  )}
                  {!item.monitored && status.secondary && <div className="muted cc-meta">{status.secondary}</div>}
                </div>
              </article>
              );
            })}
          </div>
          {inspectorItem && (
            <InspectorPanel
              item={inspectorItem}
              kind={kind}
              profiles={profiles ?? []}
              onClose={closeInspector}
              onStep={stepInspector}
              canPrev={inspectorIndex > 0}
              canNext={inspectorIndex >= 0 && inspectorIndex < filtered.length - 1}
              onSearchReleases={(it, opener) => openReleaseSearch(it.id, opener)}
              onShowFiles={(it) => { setInspectorId(null); setFilesId(it.id); }}
              onShowDetail={(it) => { setInspectorId(null); setDetailId(it.id); }}
              onRemoveRequest={(it) => setPendingRemove(it)}
              onChanged={reload}
            />
          )}
        </div>
      )}
      {view === "list" && filtered.length > 0 && (
        // The dossier renders beside the list too, so switching views never
        // loses the selection: the same title stays selected and its dossier
        // stays open in either view, and keyboard stepping follows the same
        // filtered order in both. The list title still opens the detail page
        // as before; Dossier is a separate action per row.
        <div className={inspectorItem ? "cc-workspace open" : "cc-workspace"}>
          <div className="card table-wrap">
            <table>
              <thead><tr><th /><th>Title</th><th>Year</th><th>Monitored</th><th>Profile</th><th>Status</th><th /></tr></thead>
              <tbody>
                {filtered.map((item) => {
                  const status = primaryStatus(item, kind);
                  return (
                  <tr key={item.id} className={inspectorId === item.id ? "selected" : ""}>
                    <td><Poster item={item} size="thumb" /></td>
                    <td><button className="small" style={{ border: 0, padding: 0, fontWeight: 600 }} onClick={() => setDetailId(item.id)}>{item.title}</button></td>
                    <td>{item.year ?? ""}</td>
                    <td><button className="small" onClick={() => toggle(item)}>{item.monitored ? "Yes" : "No"}</button></td>
                    <td>{profiles?.find((p) => p.id === item.qualityProfileId)?.name ?? item.qualityProfileId}</td>
                    <td>{item.monitored
                      ? (kind === "movie"
                        ? (item.hasFile ? <span className="badge ok">have</span> : <span className="badge">missing</span>)
                        : (<span className={availabilityClass(seriesAvailability(item))}>{seriesAvailability(item)}{item.applicableEpisodes != null && item.applicableEpisodes > 0 ? ` ${item.availableEpisodes ?? 0}/${item.applicableEpisodes}` : ""}</span>))
                      : (<><span className="badge">Unmonitored</span>{status.secondary && <div className="muted">{status.secondary}</div>}</>)}
                    </td>
                    <td className="row">
                      <button className="small" onClick={(e) => openInspector(item, e.currentTarget)}>Dossier</button>
                      <button className="small" onClick={() => setDetailId(item.id)}>Details</button>
                      <button className="small" onClick={() => setFilesId(filesId === item.id ? null : item.id)}>Files</button>
                      <button className="small danger" onClick={() => setPendingRemove(item)}>Remove</button>
                    </td>
                  </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {inspectorItem && (
            <InspectorPanel
              item={inspectorItem}
              kind={kind}
              profiles={profiles ?? []}
              onClose={closeInspector}
              onStep={stepInspector}
              canPrev={inspectorIndex > 0}
              canNext={inspectorIndex >= 0 && inspectorIndex < filtered.length - 1}
              onSearchReleases={(it, opener) => openReleaseSearch(it.id, opener)}
              onShowFiles={(it) => { setInspectorId(null); setFilesId(it.id); }}
              onShowDetail={(it) => { setInspectorId(null); setDetailId(it.id); }}
              onRemoveRequest={(it) => setPendingRemove(it)}
              onChanged={reload}
            />
          )}
        </div>
      )}
      {filesId !== null && <FileList mediaId={filesId} onClose={() => setFilesId(null)} />}
      {searchId !== null && <ReleaseSearchDialog mediaId={searchId} onClose={closeReleaseSearch} />}
      {pendingRemove !== null && (
        <ConfirmDialog
          title="Remove title?"
          body={`Remove "${pendingRemove.title}" from OneArr? Files and torrents are not touched.`}
          confirmLabel="Remove"
          onConfirm={confirmRemove}
          onCancel={() => setPendingRemove(null)}
        />
      )}
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
  const [managerTab, setManagerTab] = useState<"seasons" | "files" | "settings">("seasons");
  const [releaseLabel, setReleaseLabel] = useState<string | null>(null);
  const [packOnly, setPackOnly] = useState(false);

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
  const detailStatus = primaryStatus(data, isSeries ? "series" : "movie");

  return (
    <>
      <div className="row spread series-manager-toolbar"><span className="mono">ONEARR / {isSeries ? "SERIES" : "MOVIE"} MANAGER</span><button onClick={onBack} aria-label="Close media manager">✕ Close</button></div>
      <ErrorLine message={error ?? message} />
      <div className="card detail-head">
        {posterUrl(data.posterPath)
          ? <img className="poster detail-poster" src={posterUrl(data.posterPath)!} alt={`${data.title} poster`} />
          : <div className="poster poster-fallback detail-poster"><span>{data.title}</span></div>}
        <div style={{ minWidth: 0 }}>
          <h2>{data.title} {data.year ? `(${data.year})` : ""}</h2>
          <p>{data.overview || <span className="muted">No overview available.</span>}</p>
          <div className="row">
            <span className={detailStatus.badgeClass}>{detailStatus.badge}</span>
            {data.monitored && <span className="badge">Monitored</span>}
            <span className="badge">{profiles.find((p) => p.id === data.qualityProfileId)?.name ?? data.qualityProfileId}</span>
            {isSeries && <span className="badge">Wanted episodes: {wantedEpisodes}</span>}
          </div>
          {detailStatus.secondary && <p className="muted">{detailStatus.secondary}</p>}
          {data.monitored && isSeries && data.applicableEpisodes != null && data.applicableEpisodes > 0 && (
            <p className="muted">{data.availableEpisodes ?? 0} / {data.applicableEpisodes} monitored aired episodes available</p>
          )}
          {data.monitored && isSeries && (data.applicableEpisodes == null || data.applicableEpisodes === 0) && (
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
      {isSeries && managerTab === "settings" && <div className="card"><h2>Series settings</h2><p className="muted">Use the monitoring, root folder and quality profile controls above. Changes save immediately.</p></div>}
      {showReleases && !isSeries && <ReleasePanel mediaId={mediaId} episodeId={null} onClose={() => setShowReleases(false)} />}
      {isSeries && <div className="series-manager-tabs" role="tablist" aria-label="Manager sections"><button role="tab" aria-selected={managerTab === "seasons"} className={managerTab === "seasons" ? "active" : ""} onClick={() => setManagerTab("seasons")}>Seasons & episodes</button><button role="tab" aria-selected={managerTab === "files"} className={managerTab === "files" ? "active" : ""} onClick={() => setManagerTab("files")}>Linked files ({data.files.length})</button><button role="tab" aria-selected={managerTab === "settings"} className={managerTab === "settings" ? "active" : ""} onClick={() => setManagerTab("settings")}>Settings</button></div>}
      {isSeries && managerTab === "seasons" && <div className="row spread series-season-heading"><h2>Seasons</h2><div className="row"><button className="small" onClick={() => setCollapsed(new Set(data.seasons.map(s => s.seasonNumber)))}>Expand all</button><button className="small" onClick={() => setCollapsed(new Set())}>Collapse all</button></div></div>}
      {isSeries && managerTab === "seasons" && data.seasons.map((s) => {
        const eps = data.episodes.filter((e) => e.seasonNumber === s.seasonNumber);
        const isCollapsed = !collapsed.has(s.seasonNumber);
        return (
          <div key={s.seasonNumber} className="card">
            <div className="row spread">
              <h2 style={{ margin: 0 }}>Season {s.seasonNumber}</h2>
              <div className="row">
                <span className="badge">{s.monitored ? "Monitored" : "Unmonitored"}</span>
                <span className="muted">{s.hasFileCount}/{s.episodeCount} have files · {s.wantedCount} wanted</span>
                <button className="small" disabled={eps.length === 0} onClick={() => { const chosen = eps.find(e => e.monitored && !e.hasFile) ?? eps[0]; if (!chosen) return; setPackOnly(true); setReleaseLabel(`Season ${s.seasonNumber} — season packs`); setReleaseEpisode(chosen.id); }}>Search season packs</button>
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
                      <td><button className="small" aria-label={`Search releases for S${String(e.seasonNumber).padStart(2, "0")}E${String(e.episodeNumber).padStart(2, "0")}`} onClick={() => { setPackOnly(false); setReleaseLabel(`S${String(e.seasonNumber).padStart(2, "0")}E${String(e.episodeNumber).padStart(2, "0")} — ${e.title}`); setReleaseEpisode(e.id); }}>Search episode</button></td>
                    </tr>
                  ))}
                </tbody>
              </table></div>
            )}

          </div>
        );
      })}
      {(!isSeries || managerTab === "files") && <div className="card">
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
      </div>}
      {releaseEpisode !== null && <div className="dialog-backdrop release-search-backdrop"><div role="dialog" aria-modal="true" aria-label={`Episode releases: ${releaseLabel ?? ""}`} className="dialog release-search-dialog"><p className="mono">{releaseLabel}</p><ReleasePanel key={`${releaseEpisode}-${packOnly}`} mediaId={mediaId} episodeId={releaseEpisode} packOnly={packOnly} onClose={() => { setReleaseEpisode(null); setReleaseLabel(null); }} autoSearch /></div></div>}
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

function formatAge(publishDateUtc: string | null): string {
  if (!publishDateUtc) return "—";
  const ms = Date.now() - Date.parse(publishDateUtc);
  if (!Number.isFinite(ms) || ms < 0) return "—";
  const hours = ms / 3_600_000;
  if (hours < 1) return "<1h";
  if (hours < 24) return `${Math.floor(hours)}h`;
  return `${Math.floor(hours / 24)}d`;
}

function ageMs(publishDateUtc: string | null): number {
  if (!publishDateUtc) return Number.POSITIVE_INFINITY;
  const ms = Date.now() - Date.parse(publishDateUtc);
  return Number.isFinite(ms) && ms >= 0 ? ms : Number.POSITIVE_INFINITY;
}

// A search launched from a poster action or dossier must appear in the
// viewport, not below a possibly hundreds-of-posters-long library grid.
function ReleaseSearchDialog({ mediaId, onClose }: { mediaId: number; onClose: () => void }) {
  const dialogRef = useRef<HTMLDivElement>(null);
  useEffect(() => { dialogRef.current?.focus(); }, []);
  function onDialogKeyDown(e: React.KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape") {
      e.preventDefault();
      e.stopPropagation();
      onClose();
      return;
    }
    if (e.key !== "Tab") return;
    const focusable = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(
      'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])'
    ) ?? []).filter((el) => el.getClientRects().length > 0 || !el.hasAttribute("hidden"));
    if (focusable.length === 0) { e.preventDefault(); dialogRef.current?.focus(); return; }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (e.shiftKey && (document.activeElement === first || document.activeElement === dialogRef.current)) {
      e.preventDefault(); last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault(); first.focus();
    }
  }
  return (
    <div className="dialog-backdrop release-search-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div ref={dialogRef} tabIndex={-1} role="dialog" aria-modal="true"
        aria-label="Manual release search" className="dialog release-search-dialog"
        onKeyDown={onDialogKeyDown}>
        <ReleasePanel mediaId={mediaId} episodeId={null} onClose={onClose} autoSearch />
      </div>
    </div>
  );
}

export function ReleasePanel({ mediaId, episodeId, onClose, autoSearch = false, packOnly = false }: { mediaId: number; episodeId: number | null; onClose: () => void; autoSearch?: boolean; packOnly?: boolean }) {
  const [busy, setBusy] = useState(false);
  const [results, setResults] = useState<ReleaseResult[] | null>(null);
  const [outcomes, setOutcomes] = useState<IndexerSearchOutcome[]>([]);
  const [message, setMessage] = useState<string | null>(null);
  const [grabSuccess, setGrabSuccess] = useState<string | null>(null);
  const { data: clients } = useLoad<DownloadClient[]>("/api/download-clients");
  const enabledClients = (clients ?? []).filter((c) => c.enabled);
  const [clientId, setClientId] = useState<number | null>(null);
  const [indexerFilter, setIndexerFilter] = useState("all");
  const [decisionFilter, setDecisionFilter] = useState("all");
  const [minSeeders, setMinSeeders] = useState("");
  const [sort, setSort] = useState("best");
  // Default: the first enabled client. An explicit choice overrides it, and the chosen id is
  // sent with the grab so the server can validate it instead of silently picking one.
  const selectedClient = clientId ?? enabledClients[0]?.id ?? null;

  async function search() {
    setBusy(true);
    setMessage(null);
    try {
      const res = await api<{ results: ReleaseResult[]; indexers: IndexerSearchOutcome[] }>("POST", "/api/search", { mediaItemId: mediaId, episodeId, seasonPackOnly: packOnly });
      setResults(res.results);
      setOutcomes(res.indexers ?? []);
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  // The dossier's Search releases action searches immediately; inline
  // ReleasePanel usage retains the explicit Search indexers button.
  useEffect(() => {
    if (autoSearch) void search();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoSearch, mediaId, episodeId]);

  async function grab(r: ReleaseResult) {
    setMessage(null);
    setGrabSuccess(null);
    try {
      await api("POST", "/api/grab", { mediaItemId: mediaId, episodeId, title: r.title, link: r.link, infoHash: r.infoHash, downloadClientId: selectedClient, seasonPackOnly: packOnly });
      setGrabSuccess(`Sent to qBittorrent: ${r.title}. It will appear in the media library only after import completes. Check Activity → Queue for download and import progress.`);
    } catch (e) {
      setMessage((e as Error).message);
    }
  }

  const indexerNames = [...new Set((results ?? []).map((r) => r.indexer))].sort();
  // Client-side sorting and filtering only. The server re-validates every grab, so a rejected
  // release can never pass because of a frontend change.
  const visible = (results ?? []).filter((r) =>
    (!packOnly || /(?:^|[. _-])S\\d{1,3}(?:[. _-]|$)/i.test(r.title) && !/S\\d{1,3}E\\d{1,3}/i.test(r.title)) &&
    (indexerFilter === "all" || r.indexer === indexerFilter) &&
    (decisionFilter === "all" || (decisionFilter === "accepted") === r.accepted) &&
    (minSeeders === "" || r.seeders >= Number(minSeeders)));
  visible.sort((a, b) => {
    switch (sort) {
      case "quality": return (b.resolution ?? 0) - (a.resolution ?? 0);
      case "size": return b.size - a.size;
      case "seeders": return b.seeders - a.seeders;
      case "age": return ageMs(a.publishDateUtc) - ageMs(b.publishDateUtc);
      default: return 0;
    }
  });

  return (
    <div className="card" style={{ marginTop: "1rem" }}>
      <div className="row spread">
        <h2>{packOnly ? "Season pack releases" : episodeId !== null ? "Episode releases" : "Manual release search"}</h2>
        <div className="row">
          <button className="primary" onClick={search} disabled={busy}>{busy ? "Searching…" : "Search indexers"}</button>
          <button onClick={onClose}>Close</button>
        </div>
      </div>
      <p className="muted">{packOnly ? "Season-pack candidates only. OneArr validates the selected season through an episode in that season. Review pack contents before grabbing; this does not guarantee all episodes are present." : "Only releases you choose here are sent to qBittorrent. Downloading is separate from importing the video into your library."}</p>
      <ErrorLine message={message} />
      {grabSuccess && <p className="ok" role="status">{grabSuccess}</p>}
      {outcomes.length > 0 && (
        <div>
          <h3>Indexers in this search</h3>
          <ul>{outcomes.map((o) => (
            <li key={o.id}>{o.name}: {o.status === "results" ? `${o.resultCount} release(s)` : o.status === "no-results" ? "no releases" : o.status === "failed" ? `failed — ${o.message}` : `skipped — ${o.message}`}</li>
          ))}</ul>
          <p className="muted">A successful connection test is not a successful search: these are the results of this search.</p>
        </div>
      )}
      {results && results.length === 0 && <p className="muted">No releases returned.</p>}
      {results && results.length > 0 && (
        <>
          <div className="row">
            <select aria-label="Filter by indexer" value={indexerFilter} onChange={(e) => setIndexerFilter(e.target.value)}>
              <option value="all">All indexers</option>
              {indexerNames.map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
            <select aria-label="Filter by decision" value={decisionFilter} onChange={(e) => setDecisionFilter(e.target.value)}>
              <option value="all">Accepted + rejected</option><option value="accepted">Accepted only</option><option value="rejected">Rejected only</option>
            </select>
            <input aria-label="Minimum seeders" type="number" min={0} placeholder="Min seeders" value={minSeeders} onChange={(e) => setMinSeeders(e.target.value)} />
            <select aria-label="Sort releases" value={sort} onChange={(e) => setSort(e.target.value)}>
              <option value="best">Sort: best match</option><option value="quality">Sort: quality</option><option value="size">Sort: size</option><option value="seeders">Sort: seeders</option><option value="age">Sort: newest</option>
            </select>
          </div>
          {enabledClients.length > 0 ? (
            <label className="inline">Download client
              <select aria-label="Download client" value={selectedClient ?? ""} onChange={(e) => setClientId(Number(e.target.value))}>
                {enabledClients.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </label>
          ) : (
            <p className="muted">No enabled download client — add one in Settings → Download Clients before grabbing.</p>
          )}
          {visible.length === 0 && <p className="muted">No releases match the current filters.</p>}
          {visible.length > 0 && (
            <div className="table-wrap"><table>
              <thead><tr><th>Release</th><th>Indexer</th><th>Res</th><th>Src</th><th>Seeds</th><th>Size</th><th>Age</th><th>Decision</th><th /></tr></thead>
              <tbody>
                {visible.map((r, i) => (
                  <tr key={i}>
                    <td>{r.title}</td>
                    <td>{r.indexer}</td>
                    <td>{r.resolution ?? "?"}p</td>
                    <td>{r.source}</td>
                    <td>{r.seeders}</td>
                    <td>{(r.size / 1_073_741_824).toFixed(2)} GiB</td>
                    <td>{formatAge(r.publishDateUtc)}</td>
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
        </>
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
        <div className="toolbar">
          <input aria-label="Search wanted" placeholder="Search titles…" value={query} onChange={(e) => setQuery(e.target.value)} />
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

function formatSpeed(bps: number): string {
  if (!bps || bps <= 0) return "—";
  if (bps >= 1_000_000) return `${(bps / 1_000_000).toFixed(1)} MB/s`;
  if (bps >= 1_000) return `${(bps / 1_000).toFixed(0)} KB/s`;
  return `${bps} B/s`;
}

function formatEta(seconds: number): string {
  if (seconds < 0) return "—";
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function fileNameOf(path: string): string {
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] || path;
}

// Activity: Queue, History, and Problems tabs over the same download jobs. Speed and ETA are
// only ever shown when the client reported them; a dash means unknown, never an invented value.
// Previewing an import never writes anything; applying one requires the preview id plus an
// explicit confirmation, and a plan that changed since the preview is refused with a fresh one.
export function Activity({ initialTab }: { initialTab?: "queue" | "history" | "problems" }) {
  const { data: queue, error, reload } = useLoad<QueueData>("/api/queue");
  const { data: importSettings } = useLoad<SettingsData>("/api/settings");
  // Real routes live under /api (see ApiEndpoints MapGroup("/api")). The old
  // /activity/* paths returned the SPA index.html, so History/Problems never loaded.
  const { data: history, reload: reloadHistory } = useLoad<HistoryEntry[]>("/api/activity/history");
  const { data: problems, reload: reloadProblems } = useLoad<Problem[]>("/api/activity/problems");
  const { data: imports } = useLoad<any[]>("/api/imports");
  const [tab, setTab] = useState<"queue" | "history" | "problems">(initialTab ?? "queue");
  const [message, setMessage] = useState<string | null>(null);
  const [previewJobId, setPreviewJobId] = useState<number | null>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);

  async function run(label: string, path: string, body?: unknown, confirmText?: string) {
    if (confirmText && !confirm(confirmText)) return;
    setMessage(null);
    try {
      const res = await api<any>("POST", path, body);
      setMessage(res?.message ?? `${label} finished`);
      reload(); reloadHistory(); reloadProblems();
    } catch (e) {
      setMessage((e as Error).message);
    }
  }

  async function openPreview(jobId: number) {
    setPreviewJobId(jobId);
    setPreview(null);
    setPreviewBusy(true);
    setMessage(null);
    try {
      setPreview(await api<ImportPreview>("POST", `/api/queue/${jobId}/import-preview`));
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setPreviewBusy(false);
    }
  }

  async function applyPreview() {
    if (!preview || !previewJobId) return;
    if (!confirm("Apply this import? Source files are never deleted or modified; destinations are never overwritten.")) return;
    setMessage(null);
    try {
      await api("POST", `/api/queue/${previewJobId}/import-apply`, { previewId: preview.previewId, confirmed: true });
      setMessage("Import applied");
      setPreview(null);
      setPreviewJobId(null);
      reload(); reloadHistory(); reloadProblems();
    } catch (e) {
      const err = e as ApiError;
      if (err.status === 409) {
        const fresh = (err.data as { preview?: ImportPreview } | undefined)?.preview;
        if (fresh) {
          setPreview(fresh);
          setMessage("The import plan changed since the preview. Review the fresh plan below before applying.");
          return;
        }
      }
      setMessage(err.message);
    }
  }

  const jobs = queue?.jobs ?? [];
  const clients = queue?.clients ?? [];
  const unreachable = clients.filter((c) => c.lastError);

  return (
    <>
      <h2>Activity</h2>
      <div className="row">
        <button onClick={() => run("Sync", "/api/queue/sync")}>Sync from qBittorrent</button>
        <button onClick={() => run("Dry run", "/api/import?dryRun=true")}>Import dry run</button>
      </div>
      <p className="muted">Downloading or seeding a torrent does not make its movie or episode available. The library stays Missing until a completed file is imported and linked. To import manually, open the job's Preview, confirm, then Apply.</p>
      {jobs.some((job) => job.status === "Completed") && importSettings && (
        <p className="muted" role="note">
          {!importSettings.automaticImport
            ? "Automatic completed-download importing is OFF. Finished downloads require Preview import → Apply import, or opt in under Settings → Automation."
            : importSettings.dryRunImport
              ? "Automatic completed-download importing is blocked by dry-run mode. Manually preview and apply, or review Settings → Media Management → Import paths."
              : "Automatic importing is enabled. Eligible downloads are checked on the five-minute schedule; ambiguous or conflicting files still require Preview import → Apply import."}
        </p>
      )}
      <div className="row" role="tablist" aria-label="Activity sections">
        <button className={tab === "queue" ? "primary" : ""} onClick={() => setTab("queue")}>Queue</button>
        <button className={tab === "history" ? "primary" : ""} onClick={() => setTab("history")}>History</button>
        <button className={tab === "problems" ? "primary" : ""} onClick={() => setTab("problems")}>Problems{problems && problems.length > 0 ? ` (${problems.length})` : ""}</button>
      </div>
      <ErrorLine message={error ?? message} />
      {unreachable.length > 0 && tab !== "problems" && (
        <p className="error">Download client unreachable: {unreachable.map((c) => c.name).join(", ")}. Jobs on those clients are left untouched, not marked missing. See Problems.</p>
      )}
      {tab === "queue" && (
        <div className="card table-wrap">
          {jobs.length === 0 && <p className="muted">No downloads yet. Grab a release from the Movies or Series pages.</p>}
          {jobs.length > 0 && (
            <table>
              <thead><tr><th>Release</th><th>Client</th><th>Status</th><th>Progress</th><th>Speed</th><th>ETA</th><th>Import</th><th /></tr></thead>
              <tbody>
                {jobs.map((j) => (
                  <tr key={j.id}>
                    <td>{j.releaseTitle}{j.error && <div className="error">{j.error}</div>}<div className="muted">{j.clientState || "—"} · {j.category}</div></td>
                    <td>{j.clientName || "—"}</td>
                    <td>{j.status}{j.importFailed ? " (import failed)" : ""}</td>
                    <td>{Math.round(j.progress * 100)}%</td>
                    <td>{formatSpeed(j.downloadSpeedBps)}</td>
                    <td>{formatEta(j.etaSeconds)}</td>
                    <td>{j.status === "Imported" ? "Imported" : j.importReady ? "Ready to import" : j.status === "Completed" ? "Completed" : "—"}</td>
                    <td>
                      <div className="row">
                        {j.importReady && <button className="small primary" onClick={() => openPreview(j.id)}>Preview import</button>}
                        {j.status === "Failed" && j.importFailed && <button className="small" onClick={() => run("Retry import", `/api/queue/${j.id}/retry-import`, undefined, "Retry this failed import? It imports again without deleting anything.")}>Retry import</button>}
                        {j.status === "Missing" && <button className="small" onClick={() => run("Clear missing", `/api/queue/${j.id}/clear-missing`, undefined, "Clear this missing download? It only changes OneArr's record to Failed so the item can be searched or grabbed again. No torrent or file is touched.")}>Clear missing</button>}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
      {tab === "history" && (
        <div className="card table-wrap">
          {history && history.length === 0 && <p className="muted">No activity yet.</p>}
          {history && history.length > 0 && (
            <table><thead><tr><th>When</th><th>Event</th><th>Detail</th></tr></thead><tbody>
              {history.map((h, i) => (
                <tr key={i}><td className="muted">{new Date(h.at).toLocaleString()}</td><td>{h.kind}</td><td>{h.title}</td></tr>
              ))}
            </tbody></table>
          )}
        </div>
      )}
      {tab === "problems" && (
        <div className="card table-wrap">
          {problems && problems.length === 0 && <p className="muted">No problems. Downloads, clients, and imports are all in a known-good state.</p>}
          {problems && problems.length > 0 && (
            <table><thead><tr><th>Problem</th><th>Detail</th><th /></tr></thead><tbody>
              {problems.map((p, i) => (
                <tr key={i}>
                  <td>{p.title}</td>
                  <td className="muted">{p.detail}</td>
                  <td>
                    <div className="row">
                      {p.actions.includes("refresh") && <button className="small" onClick={() => run("Sync", "/api/queue/sync")}>Refresh state</button>}
                      {p.actions.includes("preview") && p.jobId && <button className="small primary" onClick={() => openPreview(p.jobId!)}>Preview import</button>}
                      {p.actions.includes("retry") && p.jobId && <button className="small" onClick={() => run("Retry import", `/api/queue/${p.jobId}/retry-import`, undefined, "Retry this failed import? It applies the latest import preview; generate a fresh preview first if the files changed. Nothing is deleted.")}>Retry failed import</button>}
                      {p.actions.includes("clear-missing") && p.jobId && <button className="small" onClick={() => run("Clear missing", `/api/queue/${p.jobId}/clear-missing`, undefined, "Clear this missing download? It only changes OneArr's record to Failed so the item can be searched or grabbed again. No torrent or file is touched.")}>Clear missing</button>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody></table>
          )}
        </div>
      )}
      {previewJobId !== null && (
        <div className="dialog-backdrop" role="presentation" onClick={(e) => { if (e.target === e.currentTarget) { setPreviewJobId(null); setPreview(null); } }}>
          <div className="dialog card" role="dialog" aria-modal="true" aria-label="Import preview">
            <div className="row spread">
              <h2>Import preview</h2>
              <button onClick={() => { setPreviewJobId(null); setPreview(null); }}>Close</button>
            </div>
            <p className="muted">A preview never moves, links, copies, renames, or deletes anything. Applying it requires confirmation below, and refuses if any file changed since this preview.</p>
            {previewBusy && <p className="muted">Planning import…</p>}
            {preview && preview.plans.length === 0 && <p className="muted">No media files found for this download.</p>}
            {preview && preview.plans.length > 0 && (
              <div className="table-wrap"><table><thead><tr><th>File</th><th>Destination</th><th>Size</th><th>Plan</th><th>Episodes</th><th>Reason</th></tr></thead><tbody>
                {preview.plans.map((p) => (
                  <tr key={p.sourcePath}>
                    <td>{fileNameOf(p.sourcePath)}</td>
                    <td className="muted">{p.destinationPath ?? "—"}</td>
                    <td>{p.sizeBytes} B</td>
                    <td>{p.plannedAction}{p.destinationConflict ? " (conflict)" : ""}{p.sameVolume ? " · same volume" : " · other volume"}</td>
                    <td>{p.matchedEpisodeLabels.length > 0 ? p.matchedEpisodeLabels.join(", ") : "—"}</td>
                    <td className="muted">{p.reason}</td>
                  </tr>
                ))}
              </tbody></table></div>
            )}
            {preview && preview.plans.length > 0 && (
              <div className="row">
                <button className="primary" onClick={applyPreview}>Apply import (confirmed)</button>
                <span className="muted">Sources are never deleted; destinations are never overwritten.</span>
              </div>
            )}
          </div>
        </div>
      )}
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
