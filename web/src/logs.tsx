import { useState } from "react";
import { api } from "./api";
import { ErrorLine, useLoad } from "./common";

interface LogEntry { time: string; level: string; category: string; message: string }
interface LogFile { name: string; lines: string[] }
interface JobHistory { id: number; name: string; startedAt: string; finishedAt: string | null; success: boolean; message: string | null }

/** Settings > Logs & diagnostics. Read-only: shows what OneArr recorded and offers one redacted file to share. */
export function LogsSection() {
  const [level, setLevel] = useState("Warning");
  const { data: logs, error, reload } = useLoad<LogEntry[]>(`/api/logs?level=${level}&limit=500`);
  const { data: files } = useLoad<{ folder: string | null; files: LogFile[] }>("/api/logs/files");
  const { data: jobs } = useLoad<JobHistory[]>("/api/jobs");
  const [message, setMessage] = useState<string | null>(null);
  const [openFile, setOpenFile] = useState<string | null>(null);

  async function download() {
    setMessage(null);
    try {
      const report = await api<unknown>("GET", "/api/diagnostics");
      const blob = new Blob([JSON.stringify(report, null, 2)], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `onearr-diagnostics-${new Date().toISOString().replace(/[:.]/g, "-")}.json`;
      a.click();
      URL.revokeObjectURL(url);
      setMessage("Diagnostics saved. API keys, passwords and indexer URLs are left out; file paths and release names are included.");
    } catch (e) { setMessage((e as Error).message); }
  }

  const failed = (jobs ?? []).filter((j) => !j.success).slice(0, 30);
  return (
    <>
      <div className="card" id="logs-download">
        <h2>Diagnostics</h2>
        <p className="muted">One file with settings (no secrets), indexer and client health, the queue, recent searches, imports, scheduled task results and recent log messages. Share it when something is not working.</p>
        <button className="primary" onClick={download}>Download diagnostics</button>
        <ErrorLine message={message} />
      </div>
      <div className="card" id="logs-recent">
        <div className="row spread">
          <h2>Recent log messages</h2>
          <span className="row">
            <select aria-label="Log level" value={level} onChange={(e) => setLevel(e.target.value)}>
              {["Information", "Warning", "Error"].map((l) => <option key={l} value={l}>{l} and above</option>)}
            </select>
            <button className="small" onClick={reload}>Refresh</button>
          </span>
        </div>
        <ErrorLine message={error} />
        {logs && logs.length === 0 && <p className="muted">Nothing at this level since OneArr started.</p>}
        {logs && logs.length > 0 && (
          <div className="table-wrap"><table><thead><tr><th>Time</th><th>Level</th><th>Message</th></tr></thead><tbody>
            {logs.map((l, i) => (
              <tr key={i}>
                <td className="muted mono">{new Date(l.time).toLocaleString()}</td>
                <td><span className={l.level === "Error" || l.level === "Critical" ? "badge bad" : l.level === "Warning" ? "badge" : "badge ok"}>{l.level}</span></td>
                <td><span className="mono">{l.message}</span><div className="muted">{l.category}</div></td>
              </tr>))}
          </tbody></table></div>)}
      </div>
      <div className="card" id="logs-tasks">
        <h2>Scheduled tasks that failed</h2>
        {failed.length === 0 ? <p className="muted">No recent failures.</p> : (
          <ul>{failed.map((j) => <li key={j.id}><strong>{j.name}</strong> <span className="muted">{new Date(j.startedAt).toLocaleString()}</span> — {j.message}</li>)}</ul>)}
      </div>
      <div className="card" id="logs-files">
        <h2>Log files</h2>
        {!files?.folder && <p className="muted">Log files are written by the Windows tray app; when OneArr runs another way they are not available here.</p>}
        {files?.files.map((f) => (
          <div key={f.name}>
            <button className="small" onClick={() => setOpenFile(openFile === f.name ? null : f.name)}>{openFile === f.name ? "Hide" : "Show"} {f.name} (last {f.lines.length} lines)</button>
            {openFile === f.name && <pre className="mono" style={{ maxHeight: 400, overflow: "auto", whiteSpace: "pre-wrap" }}>{f.lines.join("\n")}</pre>}
          </div>))}
      </div>
    </>
  );
}
