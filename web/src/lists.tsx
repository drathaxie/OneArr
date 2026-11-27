import { useState } from "react";
import { api } from "./api";
import { ErrorLine } from "./common";

interface PreviewItem { type: "movie" | "series"; providerId: string; title: string; year: number | null; inCatalog: boolean }
interface Preview { items: PreviewItem[]; notFound: string[] }
interface AddResult { added: number; alreadyThere: number; errors: string[] }

const key = (i: PreviewItem) => `${i.type}:${i.providerId}`;

/** One-off import from a TMDb list or IMDb ids. Preview first; only ticked titles are added, with the default folders and profiles. */
export function ListImport() {
  const [source, setSource] = useState<"tmdb" | "imdb">("tmdb");
  const [value, setValue] = useState("");
  const [monitored, setMonitored] = useState(true);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

  async function load() {
    setBusy(true); setMessage(null); setPreview(null);
    try {
      const p = await api<Preview>("POST", "/api/lists/preview", { source, value });
      setPreview(p);
      setPicked(new Set(p.items.filter((i) => !i.inCatalog).map(key)));
    } catch (e) { setMessage((e as Error).message); }
    finally { setBusy(false); }
  }

  async function add() {
    if (!preview) return;
    const items = preview.items.filter((i) => picked.has(key(i))).map((i) => ({ type: i.type, providerId: i.providerId }));
    if (!confirm(`Add ${items.length} title(s) to the catalog${monitored ? " as monitored" : ""}? They use your default root folders and quality profiles.${monitored ? " With automatic grabbing on, missing ones may then be searched." : ""}`)) return;
    setBusy(true); setMessage(null);
    try {
      const r = await api<AddResult>("POST", "/api/lists/add", { items, monitored });
      setMessage(`Added ${r.added}, already in the catalog ${r.alreadyThere}.${r.errors.length ? " Problems: " + r.errors.slice(0, 5).join("; ") : ""} New titles may already be on disk: run a library scan before automatic grabbing resumes.`);
      setPreview(null);
    } catch (e) { setMessage((e as Error).message); }
    finally { setBusy(false); }
  }

  return (
    <div className="card">
      <h2>Import from a list</h2>
      <p className="muted">A public TMDb list (its number or themoviedb.org/list/… address), or IMDb ids: paste an IMDb list export (CSV) or ids one per line. Nothing is added until you choose.</p>
      <div className="row">
        <select aria-label="List source" value={source} onChange={(e) => setSource(e.target.value as "tmdb" | "imdb")}>
          <option value="tmdb">TMDb list</option>
          <option value="imdb">IMDb ids</option>
        </select>
        {source === "tmdb"
          ? <input aria-label="TMDb list" value={value} onChange={(e) => setValue(e.target.value)} placeholder="https://www.themoviedb.org/list/8312345" style={{ minWidth: 320 }} />
          : <textarea aria-label="IMDb ids" value={value} onChange={(e) => setValue(e.target.value)} placeholder={"tt0133093\ntt1375666"} rows={4} style={{ minWidth: 320 }} />}
        <button disabled={busy || !value.trim()} onClick={load}>{busy && !preview ? "Loading…" : "Preview"}</button>
      </div>
      <ErrorLine message={message} />
      {preview && (
        <>
          <p className="muted">{preview.items.length} title(s){preview.notFound.length ? `, ${preview.notFound.length} IMDb id(s) not found on TMDb` : ""}.</p>
          <ul>
            {preview.items.map((i) => (
              <li key={key(i)}>
                <label className="inline">
                  <input type="checkbox" disabled={i.inCatalog} checked={picked.has(key(i))}
                    onChange={(e) => { const next = new Set(picked); if (e.target.checked) next.add(key(i)); else next.delete(key(i)); setPicked(next); }} />
                  {" "}{i.title}{i.year ? ` (${i.year})` : ""} <span className="muted">{i.type}{i.inCatalog ? " · already in the catalog" : ""}</span>
                </label>
              </li>))}
          </ul>
          <label className="inline"><input type="checkbox" checked={monitored} onChange={(e) => setMonitored(e.target.checked)} /> monitored</label>
          <div><button className="primary" disabled={busy || picked.size === 0} onClick={add}>Add {picked.size} title(s)</button></div>
        </>
      )}
    </div>
  );
}
