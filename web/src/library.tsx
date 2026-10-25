import { useEffect, useState, type FormEvent } from "react";
import { api } from "./api";

type Root = { id: number; mediaType: string; path: string; label: string | null; lastState: string; lastMessage: string | null; lastSuccessfulScanUtc: string | null };
type Mapping = { id?: number; mediaType: string; sourcePrefix: string; targetPrefix: string };
type Status = { windows: boolean; automation: { allowed: boolean; reason: string }; roots: Root[]; mappings: Mapping[] };
type Entry = { path: string; key?: string; method?: string; reason?: string; mediaType?: string; candidateIds?: number[] };
type RootReport = { id: number; path: string; state: string; message: string | null; filesFound: number; skippedReparsePoints: number; unreadableFolders: number; complete: boolean; incompleteReason: string | null };
type Plan = {
  planId: number;
  plan: {
    roots: RootReport[]; links: (Entry & { sizeBytes: number; mediaItemId: number; mediaFileId: number | null })[]; missing: Entry[];
    unknown: (Entry & { reason?: string })[]; ambiguous: Entry[]; unmatched: Entry[]; alreadyLinked: number; filesUnderUnavailableRoots: number;
    complete: boolean; warnings: string[];
  };
};
type ApplyResult = {
  linked: number; relinked: number; markedMissing: number; markedUnknown: number; markedInaccessible: number; reviewedItems: number;
  complete: boolean; skipped: string[]; automation: { allowed: boolean; reason: string };
};

const PAGE_LIMIT = 200;

/** Links and missing files still ticked. Unticked paths are sent to the server as exclusions. */
function selectedCount(plan: Plan, unchecked: Set<string>) {
  return plan.plan.links.filter((l) => !unchecked.has(l.path)).length + plan.plan.missing.filter((m) => !unchecked.has(m.path)).length;
}

function stateClass(state: string) {
  return state === "Accessible" ? "ok" : state === "Unknown" ? "muted" : "error";
}

export function LibraryPanel() {
  const [status, setStatus] = useState<Status | null>(null);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [unchecked, setUnchecked] = useState<Set<string>>(new Set());
  const [message, setMessage] = useState<string | null>(null);
  const [result, setResult] = useState<ApplyResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [acknowledge, setAcknowledge] = useState(false);

  async function refresh() {
    try {
      setStatus(await api<Status>("GET", "/api/library/status"));
    } catch (e) {
      setMessage((e as Error).message);
    }
  }

  useEffect(() => { void refresh(); }, []);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setMessage(null);
    try {
      await action();
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function preview() {
    await run(async () => {
      setResult(null);
      const res = await api<Plan>("POST", "/api/library/preview");
      setPlan(res);
      setUnchecked(new Set());
    });
  }

  async function apply() {
    if (!plan) return;
    const count = selectedCount(plan, unchecked);
    if (!confirm(`Apply ${count} change(s) to OneArr's database? No files will be moved, renamed, copied, linked or deleted.`)) return;
    await run(async () => {
      const res = await api<ApplyResult>("POST", `/api/library/plans/${plan.planId}/apply`, { excludePaths: [...unchecked], acknowledgeUnmatched: acknowledge });
      setAcknowledge(false);
      setResult(res);
      setPlan(null);
      await refresh();
    });
  }

  function toggle(path: string) {
    const next = new Set(unchecked);
    if (next.has(path)) next.delete(path);
    else next.add(path);
    setUnchecked(next);
  }

  if (!status) return <div className="card"><h2>Library</h2>{message && <p className="error">{message}</p>}</div>;

  const automationOk = status.automation.allowed;

  return (
    <>
      <div className="card">
        <h2>Automatic grabbing</h2>
        <p className={automationOk ? "ok" : "error"}>
          {automationOk ? "Ready. " : "Paused. "}{status.automation.reason}
        </p>
      </div>

      <div className="card">
        <h2>Library folders</h2>
        <p className="muted">Folders OneArr scans for existing media. Removing a folder only removes OneArr's record of it. Nothing on disk is touched.</p>
        {status.roots.length === 0 && <p className="muted">No folders yet.</p>}
        <ul>
          {status.roots.map((r) => (
            <li key={r.id} className="row spread">
              <span>
                <strong>{r.mediaType === "Movie" ? "Movies" : "Series"}</strong> <code>{r.path}</code>{r.label ? ` (${r.label})` : ""}
                <br />
                <span className={stateClass(r.lastState)}>{r.lastState}{r.lastMessage ? `: ${r.lastMessage}` : ""}</span>
                <span className="muted">{r.lastSuccessfulScanUtc ? ` · last applied ${new Date(r.lastSuccessfulScanUtc).toLocaleString()}` : " · not scanned yet"}</span>
              </span>
              <button className="small danger" disabled={busy} onClick={() => run(async () => { await api("DELETE", `/api/library/roots/${r.id}`); await refresh(); })}>Remove</button>
            </li>
          ))}
        </ul>
        <AddRootForm windows={status.windows} disabled={busy} onAdded={() => run(refresh)} />
      </div>

      <MappingsForm mappings={status.mappings} windows={status.windows} disabled={busy} onSaved={() => run(refresh)} />

      <div className="card">
        <h2>Scan and link existing media</h2>
        <p className="muted">
          Preview reads your library folders and compares them with the catalog. It changes nothing. Applying updates OneArr's database only:
          files are never moved, renamed, copied, linked or deleted.
        </p>
        <button className="primary" disabled={busy || status.roots.length === 0} onClick={preview}>Preview scan</button>
        {status.roots.length === 0 && <span className="muted"> Add a library folder first.</span>}
        {message && <p className="error">{message}</p>}
        {result && (
          <div>
            <p className={result.complete ? "ok" : "error"}>
              Applied: {result.linked} linked, {result.relinked} relinked, {result.markedMissing} marked missing, {result.markedUnknown} unknown, {result.markedInaccessible} inaccessible.
              {result.complete ? ` ${result.reviewedItems} catalog entries reviewed.` : " The scan was incomplete, so no catalog entries were reviewed."}
            </p>
            <p className={result.automation.allowed ? "ok" : "error"}>Automatic grabbing: {result.automation.reason}</p>
            {result.skipped.length > 0 && <ul>{result.skipped.map((s, i) => <li key={i} className="muted">{s}</li>)}</ul>}
          </div>
        )}
        {plan && <PlanView plan={plan} unchecked={unchecked} onToggle={toggle} busy={busy} onApply={apply} acknowledge={acknowledge} onAcknowledge={setAcknowledge} />}
      </div>
    </>
  );
}

function PlanView({ plan, unchecked, onToggle, busy, onApply, acknowledge, onAcknowledge }: {
  plan: Plan; unchecked: Set<string>; onToggle: (path: string) => void; busy: boolean; onApply: () => void;
  acknowledge: boolean; onAcknowledge: (value: boolean) => void;
}) {
  const p = plan.plan;
  const selected = selectedCount(plan, unchecked);
  return (
    <div style={{ marginTop: ".75rem" }}>
      <p>
        <strong>{p.links.length}</strong> to link · <strong>{p.missing.length}</strong> to mark missing · {p.alreadyLinked} already linked ·{" "}
        {p.ambiguous.length} ambiguous · {p.unmatched.length} unmatched
        {p.filesUnderUnavailableRoots > 0 && <> · {p.filesUnderUnavailableRoots} under unavailable folders (left unchanged)</>}
      </p>
      {p.warnings.map((w, i) => <p key={i} className="muted">{w}</p>)}
      {p.roots.map((r) => r.state !== "Accessible" && <p key={r.id} className="error">{r.path}: {r.message}</p>)}
      {p.roots.map((r) => r.state === "Accessible" && !r.complete && (
        <p key={r.id} className="error">{r.path} was only partly read ({r.incompleteReason}). Files there are not treated as missing, and automatic grabbing waits for a complete scan.</p>
      ))}
      {!p.complete && <p className="error">This scan is incomplete, so applying it will not review catalog entries or allow automatic grabbing.</p>}

      {p.links.length > 0 && (
        <>
          <h2>Will link</h2>
          <div className="table-wrap"><table>
            <thead><tr><th /><th>File</th><th>Matched by</th></tr></thead>
            <tbody>{p.links.slice(0, PAGE_LIMIT).map((l) => (
              <tr key={l.path}><td><input type="checkbox" aria-label={`Link ${l.path}`} checked={!unchecked.has(l.path)} onChange={() => onToggle(l.path)} /></td><td className="muted">{l.path}</td><td>{l.method}{l.mediaFileId ? " (update)" : ""}</td></tr>
            ))}</tbody>
          </table></div>
        </>
      )}

      {p.unknown.length > 0 && (
        <>
          <h2>Status unknown (left as it was)</h2>
          <p className="muted">The disk could not confirm these files either way. They keep their current availability, and their items are not grabbed again until a scan can read them.</p>
          <ul>{p.unknown.slice(0, PAGE_LIMIT).map((u) => <li key={u.path}><span className="muted">{u.path}</span> — {u.reason}</li>)}</ul>
        </>
      )}

      {p.missing.length > 0 && (
        <>
          <h2>Confirmed missing</h2>
          <p className="muted">These files are gone from an online folder. Their items will show as wanted again.</p>
          <ul>{p.missing.slice(0, PAGE_LIMIT).map((m) => (
            <li key={m.path}><label className="inline"><input type="checkbox" checked={!unchecked.has(m.path)} onChange={() => onToggle(m.path)} /> <span className="muted">{m.path}</span></label></li>
          ))}</ul>
        </>
      )}

      {p.ambiguous.length > 0 && (
        <>
          <h2>Ambiguous (not linked)</h2>
          <ul>{p.ambiguous.slice(0, PAGE_LIMIT).map((a) => <li key={a.path}><span className="muted">{a.path}</span> — {a.reason}</li>)}</ul>
        </>
      )}
      {p.unmatched.length > 0 && (
        <>
          <h2>Unmatched (not linked)</h2>
          <ul>{p.unmatched.slice(0, PAGE_LIMIT).map((u) => <li key={u.path}><span className="muted">{u.path}</span> — {u.reason}</li>)}</ul>
          {p.unmatched.length > PAGE_LIMIT && <p className="muted">Showing the first {PAGE_LIMIT} of {p.unmatched.length}.</p>}
        </>
      )}

      {p.unmatched.length > 0 && (
        <label className="inline" style={{ marginBottom: ".5rem" }}>
          <input type="checkbox" checked={acknowledge} onChange={(e) => onAcknowledge(e.target.checked)} />
          {" "}I have reviewed the {p.unmatched.length} unmatched file(s). They are not in the catalog. Automatic grabbing stays paused until this is acknowledged.
        </label>
      )}
      <div><button className="primary" disabled={busy || selected === 0} onClick={onApply}>Apply {selected} change(s)</button></div>
    </div>
  );
}

function AddRootForm({ windows, disabled, onAdded }: { windows: boolean; disabled: boolean; onAdded: () => void }) {
  const [type, setType] = useState<"movie" | "series">("movie");
  const [path, setPath] = useState("");
  const [label, setLabel] = useState("");
  const [err, setErr] = useState<string | null>(null);
  const example = windows ? "D:\\Media\\Movies" : "/data/media/movies";

  async function submit(e: FormEvent) {
    e.preventDefault();
    setErr(null);
    try {
      await api("POST", "/api/library/roots", { type, path, label });
      setPath("");
      setLabel("");
      onAdded();
    } catch (x) {
      setErr((x as Error).message);
    }
  }

  return (
    <form className="row" onSubmit={submit}>
      <select aria-label="Library type" value={type} onChange={(e) => setType(e.target.value as "movie" | "series")}>
        <option value="movie">Movies</option>
        <option value="series">Series</option>
      </select>
      <input aria-label="Library folder" value={path} onChange={(e) => setPath(e.target.value)} placeholder={windows ? "D:\\Media\\Movies or \\\\nas\\media\\movies" : example} required style={{ minWidth: 260 }} />
      <input aria-label="Folder label" value={label} onChange={(e) => setLabel(e.target.value)} placeholder="Label (optional)" />
      <button className="small" type="submit" disabled={disabled}>Add folder</button>
      {err && <span className="error">{err}</span>}
    </form>
  );
}

type Suggestions = { suggestions: { mediaType: string; sourcePrefix: string; count: number }[]; entriesWithoutLegacyPath: number };

function MappingsForm({ mappings, windows, disabled, onSaved }: { mappings: Mapping[]; windows: boolean; disabled: boolean; onSaved: () => void }) {
  const [rows, setRows] = useState<Mapping[]>(mappings);
  const [err, setErr] = useState<string | null>(null);
  const [suggested, setSuggested] = useState<Suggestions | null>(null);
  useEffect(() => setRows(mappings), [mappings]);
  useEffect(() => {
    api<Suggestions>("GET", "/api/library/mapping-suggestions").then(setSuggested).catch(() => setSuggested(null));
  }, [mappings]);

  async function save() {
    setErr(null);
    try {
      await api("PUT", "/api/library/mappings", rows.map((r) => ({ type: r.mediaType === "Series" ? "series" : "movie", sourcePrefix: r.sourcePrefix, targetPrefix: r.targetPrefix })));
      onSaved();
    } catch (x) {
      setErr((x as Error).message);
    }
  }

  return (
    <div className="card">
      <h2>Path mappings</h2>
      <p className="muted">
        Sonarr and Radarr store paths as they see them, often Linux container paths such as /data/media/movies. Map each one to the matching folder on this PC.
        OneArr never uses a stored Linux path directly on Windows.
      </p>
      {suggested && suggested.suggestions.length > 0 && (
        <div style={{ marginBottom: ".75rem" }}>
          <p className="muted">Found in your stored paths. Choose the matching folder on this PC:</p>
          <ul>
            {suggested.suggestions.map((g) => (
              <li key={`${g.mediaType}${g.sourcePrefix}`} className="row spread">
                <span><code>{g.sourcePrefix}</code> <span className="muted">{g.count} {g.mediaType === "Series" ? "series" : "movies"}</span></span>
                <button className="small" disabled={rows.some((r) => r.sourcePrefix === g.sourcePrefix)}
                  onClick={() => setRows([...rows, { mediaType: g.mediaType, sourcePrefix: g.sourcePrefix, targetPrefix: "" }])}>Add mapping</button>
              </li>
            ))}
          </ul>
        </div>
      )}
      {suggested && suggested.entriesWithoutLegacyPath > 0 && (
        <p className="muted">
          {suggested.entriesWithoutLegacyPath} entries have no stored original path. Run the Radarr or Sonarr migration again to store them.
          Re-importing keeps your existing edits and does not create duplicates.
        </p>
      )}
      {rows.map((r, i) => (
        <div key={i} className="row" style={{ marginBottom: ".5rem" }}>
          <select aria-label="Mapping type" value={r.mediaType === "Series" ? "series" : "movie"} onChange={(e) => setRows(rows.map((x, j) => j === i ? { ...x, mediaType: e.target.value === "series" ? "Series" : "Movie" } : x))}>
            <option value="movie">Movies</option>
            <option value="series">Series</option>
          </select>
          <input aria-label="Source path" placeholder="/data/media/movies" value={r.sourcePrefix} onChange={(e) => setRows(rows.map((x, j) => j === i ? { ...x, sourcePrefix: e.target.value } : x))} style={{ minWidth: 200 }} />
          <span>→</span>
          <input aria-label="This PC path" placeholder={windows ? "D:\\Media\\Movies" : "/mnt/media/movies"} value={r.targetPrefix} onChange={(e) => setRows(rows.map((x, j) => j === i ? { ...x, targetPrefix: e.target.value } : x))} style={{ minWidth: 200 }} />
          <button className="small danger" onClick={() => setRows(rows.filter((_, j) => j !== i))}>Remove</button>
        </div>
      ))}
      <div className="row">
        <button className="small" onClick={() => setRows([...rows, { mediaType: "Movie", sourcePrefix: "", targetPrefix: "" }])}>Add mapping</button>
        <button className="small primary" disabled={disabled} onClick={save}>Save mappings</button>
        {err && <span className="error">{err}</span>}
      </div>
    </div>
  );
}

/** Moves catalog entries to another root folder. It changes OneArr's record only; files on disk are not moved. */
export function RootFolderTool({ kind, onChanged }: { kind: "movie" | "series"; onChanged: () => void }) {
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [folders, setFolders] = useState<{ rootFolder: string; count: number }[]>([]);

  useEffect(() => {
    api<Suggestions & { rootFolders: { rootFolder: string; count: number }[] }>("GET", "/api/library/mapping-suggestions")
      .then((res) => setFolders(res.rootFolders))
      .catch(() => setFolders([]));
  }, []);

  async function change() {
    setMessage(null);
    if (!confirm(`Change the root folder for matching ${kind === "movie" ? "movies" : "series"} to "${to}"? Files on disk are not moved.`)) return;
    try {
      const res = await api<{ updated: number }>("POST", "/api/media/root-folder", { mediaType: kind, fromRootFolder: from, toRootFolder: to, ids: null });
      setMessage(`Updated ${res.updated} entr${res.updated === 1 ? "y" : "ies"}.`);
      onChanged();
    } catch (x) {
      setMessage((x as Error).message);
    }
  }

  return (
    <details className="card">
      <summary>Change root folder in bulk</summary>
      <p className="muted">Choose the current root folder, then the new one. Only where future downloads go changes. Existing files are not moved.</p>
      <div className="row">
        <input aria-label="Current root folder" list={`roots-${kind}`} value={from} onChange={(e) => setFrom(e.target.value)} placeholder="Current folder, e.g. /data/media/movies" style={{ minWidth: 220 }} />
        <datalist id={`roots-${kind}`}>{folders.map((f) => <option key={f.rootFolder} value={f.rootFolder}>{f.count}</option>)}</datalist>
        <span>→</span>
        <input aria-label="New root folder" value={to} onChange={(e) => setTo(e.target.value)} placeholder="New folder, e.g. D:\\Media\\Movies" style={{ minWidth: 220 }} />
        <button className="small primary" disabled={!from.trim() || !to.trim()} onClick={change}>Change root folder</button>
      </div>
      {message && <p className="muted">{message}</p>}
    </details>
  );
}
