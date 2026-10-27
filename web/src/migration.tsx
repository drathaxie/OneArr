import { useState, type FormEvent } from "react";
import { api, type ConnectionTestResult, type QualityProfile } from "./api";
import { ErrorLine, useLoad } from "./common";

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
      <form className="card toolbar" onSubmit={loadPreview}>
        <select aria-label="Legacy application" value={kind} onChange={(e) => { setKind(e.target.value as "sonarr" | "radarr"); clearSyncPreview(); }}>
          <option value="radarr">Radarr (movies)</option>
          <option value="sonarr">Sonarr (series)</option>
        </select>
        <input aria-label="Legacy base URL" placeholder="http://radarr:7878" value={url} onChange={(e) => { setUrl(e.target.value); clearSyncPreview(); }} required />
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
                <li key={i}>{c.title} ({c.type}): monitored {c.seriesMonitoredBefore ? "on" : "off"} → {c.seriesMonitoredAfter ? "on" : "off"} · {c.seasonsChanged} season(s){c.seasonChanges?.length ? ` (${c.seasonChanges.map((s: any) => `S${s.seasonNumber} ${s.before ? "on" : "off"}→${s.after ? "on" : "off"}`).join(", ")})` : ""} · {c.episodesChanged} episode(s){c.episodeChanges?.length ? ` (${c.episodeChanges.map((e: any) => `S${e.seasonNumber}E${e.episodeNumber} ${e.before ? "on" : "off"}→${e.after ? "on" : "off"}`).join(", ")})` : ""}{c.episodesUnknown ? " · episode data unavailable, episodes left alone" : ""}</li>
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
