import { useState, type FormEvent } from "react";
import { api, type MetadataResult } from "./api";

type RematchSummary = { title: string; year: number | null; episodesUpdated: number; episodesAdded: number; episodesUnmonitored: number; filesUnlinked: number };

/** Points an existing entry at the correct TMDb title. Files on disk are never changed; library links are cleared for a rescan. */
export function FixMatchPanel({ item, kind, onDone, onClose }: { item: { id: number; title: string; year: number | null; providerId?: string }; kind: "movie" | "series"; onDone: () => void; onClose: () => void }) {
  const [query, setQuery] = useState(item.title);
  const [results, setResults] = useState<MetadataResult[]>([]);
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

  async function useMatch(r: MetadataResult) {
    if (r.providerId === item.providerId) {
      setMessage("This is already the matched entry.");
      return;
    }
    const label = `"${item.title}" to "${r.title}"${r.year ? ` (${r.year})` : ""}`;
    if (!confirm(`Change ${label}?\n\nFiles on disk are not changed. Library links for this ${kind} are cleared, so run Library > Preview scan and Apply to link them again. Automatic grabbing stays paused until you do.`)) return;
    setBusy(true);
    setMessage(null);
    try {
      const res = await api<RematchSummary>("PUT", `/api/media/${item.id}/match`, { providerId: r.providerId });
      const episodes = kind === "series"
        ? ` Episodes: ${res.episodesUpdated} updated, ${res.episodesAdded} added, ${res.episodesUnmonitored} no longer listed (unmonitored, history kept).`
        : "";
      setMessage(`Now matched to ${res.title}.${episodes} ${res.filesUnlinked} library link(s) cleared.`);
      onDone();
    } catch (err) {
      setMessage((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="card">
      <h2>Fix match: {item.title}</h2>
      <p className="muted">
        Currently matched to TMDb {item.providerId}{item.year ? `, ${item.year}` : ""}. Search for the correct title and choose it.
        Downloads and history stay attached to this entry.
      </p>
      <form className="row" onSubmit={search}>
        <input aria-label="Search TMDb to fix match" value={query} onChange={(e) => setQuery(e.target.value)} style={{ flex: 1, minWidth: 220 }} />
        <button className="small" type="submit" disabled={busy || !query.trim()}>Search</button>
        <button className="small" type="button" onClick={onClose}>Close</button>
      </form>
      {results.length > 0 && (
        <ul>
          {results.map((r) => (
            <li key={r.providerId} className="row spread">
              <span>{r.title}{r.year ? ` (${r.year})` : ""} <span className="muted">TMDb {r.providerId}</span></span>
              <button className="small primary" disabled={busy} onClick={() => useMatch(r)}>Use this match</button>
            </li>
          ))}
        </ul>
      )}
      {message && <p className="muted" role="status">{message}</p>}
    </div>
  );
}
