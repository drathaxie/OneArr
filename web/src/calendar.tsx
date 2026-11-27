import { useLoad, ErrorLine } from "./common";

interface CalendarEntry {
  kind: "episode" | "movie";
  mediaItemId: number;
  episodeId: number | null;
  title: string;
  detail: string | null;
  date: string;
  airsUtc: string | null;
  hasFile: boolean;
  monitored: boolean;
  downloading: boolean;
}

function dayLabel(iso: string): string {
  const d = new Date(iso + "T00:00:00");
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const diff = Math.round((d.getTime() - today.getTime()) / 86400000);
  const name = d.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "short" });
  return diff === 0 ? `Today · ${name}` : diff === 1 ? `Tomorrow · ${name}` : diff === -1 ? `Yesterday · ${name}` : name;
}

function state(e: CalendarEntry, now: number): { label: string; cls: string } {
  if (e.hasFile) return { label: "On disk", cls: "badge ok" };
  if (e.downloading) return { label: "Downloading", cls: "badge" };
  if (!e.monitored) return { label: "Not monitored", cls: "badge" };
  const aired = e.airsUtc ? new Date(e.airsUtc).getTime() <= now : new Date(e.date + "T23:59:59").getTime() <= now;
  if (e.kind === "movie") return { label: e.detail ?? "Release", cls: "badge" };
  return aired ? { label: "Missing", cls: "badge bad" } : { label: "Upcoming", cls: "badge" };
}

/** Last week and the next four weeks of monitored episodes and movie release dates. Air times are shown in local time. */
export function Calendar() {
  const { data, error } = useLoad<CalendarEntry[]>("/api/calendar");
  const now = Date.now();
  const days = new Map<string, CalendarEntry[]>();
  for (const e of data ?? []) days.set(e.date, [...(days.get(e.date) ?? []), e]);
  return (
    <>
      <h2>Calendar</h2>
      <p className="muted">Monitored series and movies from last week to four weeks ahead. Times come from TVmaze when known and are shown in your time zone; otherwise only the date is known.</p>
      <ErrorLine message={error} />
      {data && data.length === 0 && <p className="muted">Nothing scheduled in this window.</p>}
      {[...days.entries()].map(([date, entries]) => (
        <div key={date} className="card">
          <h3>{dayLabel(date)}</h3>
          <ul>
            {entries.map((e) => {
              const s = state(e, now);
              return (
                <li key={`${e.kind}-${e.mediaItemId}-${e.episodeId ?? e.detail}`} className="row spread">
                  <span>
                    {e.airsUtc && <span className="mono muted">{new Date(e.airsUtc).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })} </span>}
                    <strong>{e.title}</strong>{e.kind === "episode" && e.detail ? <span className="muted"> — {e.detail}</span> : null}
                  </span>
                  <span className={s.cls}>{s.label}</span>
                </li>);
            })}
          </ul>
        </div>))}
    </>
  );
}
