import { useEffect, useRef, useState } from "react";
import { api, formatBytes, posterUrl, type MediaDetail, type MediaItem, type Problem, type QualityProfile, type QueueData } from "./api";
import { availabilityStamp, ErrorLine, useLoad } from "./common";

export type CommandNavigate = (
  page: "dashboard" | "movies" | "series" | "wanted" | "activity" | "settings" | "migration",
  opts?: { activityTab?: "queue" | "history" | "problems"; settingsSection?: string },
) => void;

/** Operational ledger for the Movies/Series pages (refined Concept A: a ruled
 * ledger row, not four tiles and not an expandable strip — the Dashboard already
 * holds the detail). Every number comes from a real endpoint (dashboard, queue,
 * problems); if any of them fails, the ledger hides rather than showing a
 * fabricated or stale number, and the library itself still works. Every segment
 * is a button that navigates to the real page that handles it — never a dead
 * action. States are differentiated on purpose: healthy is green, a count of
 * wanted items is teal (information, not health), not-configured and problems
 * are amber, and idle / none are grey — red is reserved for destructive actions. */
export function OpsStrip({ onNavigate }: { onNavigate?: CommandNavigate }) {
  const { data: dashboard } = useLoad<any>("/api/dashboard");
  const { data: queue } = useLoad<QueueData>("/api/queue");
  // NOTE: the real route is /api/activity/problems. The frontend previously called
  // /activity/problems, which the server answers with the SPA index.html (HTTP 200),
  // so the JSON parse failed and the strip hid itself. Same fix applied to Activity's
  // history/problems loads in pages.tsx.
  const { data: problems } = useLoad<Problem[]>("/api/activity/problems");
  if (!dashboard || !queue || !problems) return null;

  const unreachable = (queue.clients ?? []).filter((c) => c.lastError);
  const healthy = problems.length === 0 && unreachable.length === 0;
  const wantedTotal = (dashboard.wantedMovies ?? 0) + (dashboard.wantedEpisodes ?? 0);
  const problemKinds = new Map<string, number>();
  for (const p of problems) problemKinds.set(p.kind, (problemKinds.get(p.kind) ?? 0) + 1);
  const problemDetail = problems.length === 0
    ? "No problems reported"
    : [...problemKinds.entries()].map(([k, n]) => `${n} ${k.replace(/-/g, " ")}`).join(" · ");
  const clients = queue.clients ?? [];
  // Downloads status, in precedence order — a client that exists but cannot be
  // reached must never be reported as "Not configured" (nothing is missing
  // from the configuration) or "Idle" (nothing is known to be idle). All
  // clients unreachable → Connection failed; some unreachable → the value
  // still reflects the reachable clients, and the detail names the failed one.
  const allUnreachable = clients.length > 0 && unreachable.length === clients.length;
  const downloadsValue = clients.length === 0
    ? { dot: "cc-dot warn", text: "Not configured" }
    : allUnreachable
      ? { dot: "cc-dot warn", text: "Connection failed" }
      : dashboard.activeDownloads > 0
        ? { dot: "cc-dot info", text: `${dashboard.activeDownloads} active` }
        : { dot: "cc-dot idle", text: "Idle" };
  const downloadsDetail = unreachable.length > 0
    ? `${unreachable.map((c) => c.name).join(", ")} unreachable`
    : clients.length > 0 ? "qBittorrent · connected" : "No download client";

  return (
    <div className="cc-ledger" aria-label="Library operations">
      <button className="cc-ledger-seg" onClick={() => onNavigate?.("settings", { settingsSection: "media" })}>
        <span className="cc-ledger-label mono">LIBRARY HEALTH</span>
        <span className="cc-ledger-value"><span className={healthy ? "cc-dot ok" : "cc-dot warn"} />{healthy ? "Healthy" : "Needs attention"}</span>
        <span className="cc-ledger-detail mono">{dashboard.movies} movies · {dashboard.series} series</span>
        <span className="cc-ledger-action">Review scan ›</span>
      </button>
      <button className="cc-ledger-seg" onClick={() => onNavigate?.("wanted")}>
        <span className="cc-ledger-label mono">WANTED</span>
        <span className="cc-ledger-value"><span className="cc-dot info" />{wantedTotal} items</span>
        <span className="cc-ledger-detail mono">Monitored, no file</span>
        <span className="cc-ledger-action">View Wanted ›</span>
      </button>
      <button className="cc-ledger-seg" onClick={() => onNavigate?.("activity", { activityTab: "queue" })}>
        <span className="cc-ledger-label mono">ACTIVE DOWNLOADS</span>
        <span className="cc-ledger-value"><span className={downloadsValue.dot} />{downloadsValue.text}</span>
        <span className="cc-ledger-detail mono">{downloadsDetail}</span>
        <span className="cc-ledger-action">Sync queue ›</span>
      </button>
      <button className="cc-ledger-seg" onClick={() => onNavigate?.("activity", { activityTab: "problems" })}>
        <span className="cc-ledger-label mono">PROBLEMS</span>
        <span className="cc-ledger-value"><span className={problems.length > 0 ? "cc-dot warn" : "cc-dot idle"} />{problems.length === 0 ? "None" : `${problems.length} need attention`}</span>
        <span className="cc-ledger-detail mono">{problemDetail}</span>
        <span className="cc-ledger-action">Review problems ›</span>
      </button>
    </div>
  );
}

function fileNameOf(path: string): string {
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] || path;
}

// Same path-shape check the Add dialog already uses for root folders: a drive path,
// a UNC path, or a POSIX path. An invalid value is rejected in the dossier with an
// inline error and is never sent to the server.
const ROOT_PATH_RE = /^([A-Za-z]:[\\]|\\\\|\/)/;

/** The dossier uses the same stamp rule as the cards (common.availabilityStamp):
 * the stamp states the file fact, monitoring rides alongside it as separate
 * mono text, so the two facts can never be confused — and a file that exists
 * reads "Available" whether the title is monitored or not. */

/** Right-side dossier: the main operational workspace for a selected title —
 * monitoring, availability, quality profile, root folder, linked files, in-place
 * details, and actions — all inside the library page. Opening it never navigates
 * away from Movies/Series. The full detail page still exists, reached from the
 * dossier's Full details action and from the poster's ••• menu, unchanged.
 *
 * Layout (refinement 1): the panel is a flex column. The head (DOSSIER + ‹ › ×)
 * and the actions footer are fixed; the middle scrolls independently, with the
 * identity block sticky at its top — so the title, profile, and file facts stay
 * visible while the sections scroll, and the primary action (Search releases) is
 * always reachable without scrolling to the foot.
 *
 * Safety: monitoring, profile, and root edits use the exact PATCH /api/media/{id}
 * the detail view already uses, with the same server re-validation; a failed PATCH
 * shows its error and the displayed values revert on reload. Nothing here touches
 * automatic grabbing, imports, migration, files, or torrents. Remove is only ever
 * requested via onRemoveRequest, which the page turns into a confirmation dialog. */
export function InspectorPanel({ item, kind, profiles, onClose, onStep, canPrev, canNext, onSearchReleases, onShowFiles, onShowDetail, onRemoveRequest, onChanged }: {
  item: MediaItem;
  kind: "movie" | "series";
  profiles: QualityProfile[];
  onClose: () => void;
  onStep: (dir: -1 | 1) => void;
  canPrev: boolean;
  canNext: boolean;
  onSearchReleases: (item: MediaItem, opener: HTMLButtonElement) => void;
  onShowFiles: (item: MediaItem) => void;
  onShowDetail: (item: MediaItem) => void;
  onRemoveRequest: (item: MediaItem) => void;
  onChanged: () => void;
}) {
  const { data, error, reload } = useLoad<MediaDetail>(`/api/media/${item.id}/detail`, [item.id]);
  const [message, setMessage] = useState<string | null>(null);
  const [root, setRoot] = useState<string | null>(null);
  const closeRef = useRef<HTMLButtonElement>(null);

  // Focus management, part 1: opening the dossier moves focus into it (the close
  // button). Stepping to another title does not remount this panel, so focus stays
  // on the ‹ / › button the user was already using. Closing returns focus to the
  // poster button that opened it — that half lives in MediaPage.closeInspector.
  useEffect(() => { closeRef.current?.focus(); }, []);
  useEffect(() => { setRoot(null); setMessage(null); }, [item.id]);

  async function patch(body: Record<string, unknown>) {
    setMessage(null);
    try {
      await api("PATCH", `/api/media/${item.id}`, body);
      reload();
      onChanged();
    } catch (e) {
      setMessage((e as Error).message);
    }
  }

  function saveRoot(e: React.FormEvent) {
    e.preventDefault();
    const value = root ?? data?.rootFolder ?? "";
    if (!ROOT_PATH_RE.test(value)) {
      setMessage("Enter a folder path, e.g. D:\\Media\\Movies");
      return;
    }
    patch({ rootFolder: value });
  }

  const profile = profiles.find((p) => p.id === item.qualityProfileId);
  const identityStamp = availabilityStamp(item, kind);
  const isSeries = kind === "series";
  const wantedEpisodes = data ? data.seasons.reduce((n, s) => n + s.wantedCount, 0) : 0;
  // File fact, kept strictly apart from the configured profile: the profile is
  // what OneArr should look for; this line is what is actually on disk.
  const fileLine = !data ? null : data.files.length === 0 ? "FILE  —"
    : kind === "movie"
      ? `FILE  ${data.files[0].resolution ? `${data.files[0].resolution}p` : "Present"} · ${formatBytes(data.files[0].sizeBytes)}`
      : `FILE  ${data.files.length} linked files`;
  const totalBytes = data ? data.files.reduce((n, f) => n + f.sizeBytes, 0) : 0;

  return (
    <aside className="cc-dossier" aria-label={`Dossier for ${item.title}`}>
      <div className="cc-dossier-head">
        <span className="mono">DOSSIER</span>
        <span className="row">
          <button aria-label="Previous title" disabled={!canPrev} onClick={() => onStep(-1)}>‹</button>
          <button aria-label="Next title" disabled={!canNext} onClick={() => onStep(1)}>›</button>
          <button ref={closeRef} aria-label="Close dossier" onClick={onClose}>×</button>
        </span>
      </div>
      <div className="cc-dossier-scroll">
        <div className="cc-dossier-identity">
          {posterUrl(item.posterPath)
            ? <img className="cc-dossier-poster" src={posterUrl(item.posterPath)!} alt={`${item.title} poster`} />
            : <div className="cc-dossier-poster cc-dossier-fallback"><span>{item.title}</span></div>}
          <div style={{ minWidth: 0 }}>
            <h3 className="cc-dossier-title serif">{item.title}</h3>
            <p className="cc-dossier-meta mono">{item.year ?? ""} · {kind === "movie" ? "MOVIE" : "SERIES"}</p>
            <p className="cc-dossier-meta mono">PROFILE  {profile?.name ?? item.qualityProfileId}</p>
            {fileLine && <p className="cc-dossier-meta mono">{fileLine}</p>}
            <p style={{ marginTop: ".5rem" }}><span className={identityStamp.cls}>{identityStamp.text}</span></p>
          </div>
        </div>
        <ErrorLine message={error ?? message} />
        {!data && <p className="muted">Loading details…</p>}
        {data && (
          <>
            <div className="cc-dossier-sec">
              <div className="cc-dossier-sec-head"><span className="cc-dossier-sec-num mono">01</span><span className="cc-dossier-sec-title">MONITORING</span></div>
              <div className="cc-switch-row">
                <button
                  className={data.monitored ? "cc-switch on" : "cc-switch"}
                  role="switch"
                  aria-checked={data.monitored}
                  aria-label="Monitored"
                  onClick={() => patch({ monitored: !data.monitored })}
                ><span className="cc-knob" /></button>
                <div>
                  <div style={{ fontWeight: 650 }}>{data.monitored ? "Monitored" : "Unmonitored"}</div>
                  <div className="muted cc-insp-note">{data.monitored ? "New releases for this title will be watched." : "This title is deliberately not wanted."}</div>
                </div>
              </div>
            </div>
            <div className="cc-dossier-sec">
              <div className="cc-dossier-sec-head"><span className="cc-dossier-sec-num mono">02</span><span className="cc-dossier-sec-title">AVAILABILITY</span></div>
              <div className="cc-avail-box">
                {(() => { const s = availabilityStamp(data, kind); return <span className={s.cls}>{s.text}</span>; })()}
                <p>{data.hasFile ? "A linked file is present for this title, so it is not wanted." : data.monitored ? "No file is linked yet, so this title is wanted and will appear in Wanted." : "No file is linked, and this title is deliberately unmonitored — so it is not reported as missing and never appears in Wanted."}</p>
                {isSeries && <p className="muted">{data.availableEpisodes ?? 0} / {data.applicableEpisodes ?? 0} monitored aired episodes available · {wantedEpisodes} wanted episodes</p>}
              </div>
            </div>
            <div className="cc-dossier-sec">
              <div className="cc-dossier-sec-head"><span className="cc-dossier-sec-num mono">03</span><span className="cc-dossier-sec-title">QUALITY &amp; LOCATION</span></div>
              <select aria-label="Quality profile" value={data.qualityProfileId} onChange={(e) => patch({ qualityProfileId: Number(e.target.value) })}>
                {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
              <p className="muted cc-insp-note mono" style={{ textTransform: "uppercase" }}>
                {(() => { const p = profiles.find((x) => x.id === data.qualityProfileId); return p ? `Min ${p.minResolution}p · Cutoff ${p.cutoffResolution}p · ${p.allowWebSources ? "Web allowed" : "Web not allowed"}` : ""; })()}
              </p>
              <form onSubmit={saveRoot} className="cc-root-group">
                <input aria-label="Root folder" value={root ?? data.rootFolder} onChange={(e) => setRoot(e.target.value)} />
                <button className="primary" type="submit" aria-label="Save root folder">Save</button>
              </form>
              <p className="muted cc-insp-note">Root folder for this title only. Saved defaults in Settings are untouched.</p>
              {data.legacyPath && <p className="muted cc-insp-note">Imported from <code>{data.legacyPath}</code> ({data.legacySource})</p>}
            </div>
            <div className="cc-dossier-sec">
              <div className="cc-dossier-sec-head"><span className="cc-dossier-sec-num mono">04</span><span className="cc-dossier-sec-title">FILES</span></div>
              {data.files.length === 0
                ? <p className="muted">No linked files yet. Run a library scan in Settings, or import a download, and they will appear here.</p>
                : (
                  <table className="cc-file-table mono">
                    <thead><tr><th>FILE</th><th style={{ textAlign: "right" }}>RES</th><th style={{ textAlign: "right" }}>SIZE</th><th style={{ textAlign: "right" }}>STATUS</th></tr></thead>
                    <tbody>
                      {data.files.map((f) => (
                        <tr key={f.path}>
                          <td className="cc-file-name" title={f.path}>{fileNameOf(f.path)}</td>
                          <td className="cc-file-num">{f.resolution ? `${f.resolution}p` : "—"}</td>
                          <td className="cc-file-num">{formatBytes(f.sizeBytes)}</td>
                          <td className="cc-file-state">{f.status}</td>
                        </tr>
                      ))}
                      <tr className="cc-file-total">
                        <td>TOTAL</td>
                        <td className="cc-file-num">—</td>
                        <td className="cc-file-num">{formatBytes(totalBytes)}</td>
                        <td className="cc-file-state">{data.files.length} file{data.files.length > 1 ? "s" : ""}</td>
                      </tr>
                    </tbody>
                  </table>
                )}
            </div>
            <div className="cc-dossier-sec">
              <div className="cc-dossier-sec-head"><span className="cc-dossier-sec-num mono">05</span><span className="cc-dossier-sec-title">FULL DETAILS</span></div>
              <p className="cc-overview">{data.overview || <span className="muted">No overview available.</span>}</p>
              <p className="muted cc-insp-note">Shown here, in place — the library grid stays open behind this dossier.</p>
            </div>
            <div className="cc-kbd-hint mono">
              <span><kbd>Esc</kbd> close dossier</span>
              <span><kbd>‹</kbd> <kbd>›</kbd> previous / next title</span>
              <span><kbd>Tab</kbd> through dossier actions</span>
            </div>
          </>
        )}
      </div>
      <div className="cc-dossier-actions">
        <button className="primary" onClick={(e) => onSearchReleases(item, e.currentTarget)}>Search releases</button>
        <div className="row" style={{ marginTop: ".6rem" }}>
          <button style={{ flex: 1 }} onClick={() => onShowFiles(item)}>Files</button>
          <button style={{ flex: 1 }} onClick={() => onShowDetail(item)}>Full details</button>
        </div>
        <div className="cc-danger-row">
          <button className="danger" style={{ width: "100%" }} onClick={() => onRemoveRequest(item)}>Remove title…</button>
          <p className="muted cc-insp-note">Remove opens the redesigned confirmation dialog. Nothing here applies without its usual confirmation; files and torrents are never touched by Remove.</p>
        </div>
      </div>
    </aside>
  );
}
