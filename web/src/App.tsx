import { useEffect, useState } from "react";
import { api, getToken, setToken, type SettingsData } from "./api";
import { Dashboard, Movies, Series, Wanted, Activity, Settings, Migration } from "./pages";
import { useLoad } from "./common";
import type { CommandNavigate } from "./command-center";

type Page = "dashboard" | "movies" | "series" | "wanted" | "activity" | "settings" | "migration";

// Archive index: numbered groups, counts in mono on the right. Counts come from
// the real dashboard endpoint and are only rendered once loaded — never a
// placeholder number. Pages without a meaningful count render no count at all.
const NAV_GROUPS: { num: string; heading: string; items: { id: Page; label: string; count?: (d: any) => number | undefined }[] }[] = [
  { num: "01", heading: "Library", items: [
    { id: "dashboard", label: "Dashboard" },
    { id: "movies", label: "Movies", count: (d) => d.movies },
    { id: "series", label: "Series", count: (d) => d.series },
  ] },
  { num: "02", heading: "Operations", items: [
    { id: "wanted", label: "Wanted", count: (d) => (d.wantedMovies ?? 0) + (d.wantedEpisodes ?? 0) },
    { id: "activity", label: "Activity" },
  ] },
  { num: "03", heading: "System", items: [
    { id: "settings", label: "Settings" },
    { id: "migration", label: "Migration Preview" },
  ] },
];

export function App() {
  const [auth, setAuth] = useState<"loading" | "setup" | "login" | "in">("loading");
  const [page, setPage] = useState<Page>("dashboard");
  // Landing state for ops-strip navigation: Activity opens on its Problems tab when
  // the strip's Problems action is used, and Settings opens on Media Management when
  // its Library health action is used. Both components mount fresh on navigation, so
  // these initial values are enough — no cross-page tab state is kept.
  const [activityTab, setActivityTab] = useState<"queue" | "history" | "problems">("queue");
  const [settingsSection, setSettingsSection] = useState<string | undefined>(undefined);

  const navigate: CommandNavigate = (next, opts) => {
    if (opts?.activityTab) setActivityTab(opts.activityTab);
    if (opts?.settingsSection) setSettingsSection(opts.settingsSection);
    setPage(next);
  };

  // Shell data, reloaded on navigation so counts stay fresh after an add or a
  // removal elsewhere. Either load failing simply hides its element — the shell
  // never invents a count or a safety state.
  const { data: dashboard } = useLoad<any>("/api/dashboard", [page]);
  const { data: settings } = useLoad<SettingsData>("/api/settings", [page]);

  useEffect(() => {
    api<{ configured: boolean; authenticated: boolean }>("GET", "/api/auth/status")
      .then((s) => setAuth(!s.configured ? "setup" : getToken() && s.authenticated ? "in" : "login"))
      .catch(() => setAuth("login"));
  }, []);

  if (auth === "loading") return <div className="center muted">Loading…</div>;
  if (auth !== "in") return <AuthForm mode={auth} onDone={() => setAuth("in")} />;

  return (
    <div className="shell">
      <nav className="sidebar" aria-label="Main">
        <div className="brand serif"><span className="brand-mark" aria-hidden="true">●</span> OneArr</div>
        {NAV_GROUPS.map((group) => (
          <div key={group.heading} className="nav-group">
            <div className="nav-heading mono"><b>{group.num}</b> — {group.heading.toUpperCase()}</div>
            {group.items.map((n) => {
              const count = dashboard && n.count ? n.count(dashboard) : undefined;
              return (
                <button
                  key={n.id}
                  className={n.id === page ? "nav active" : "nav"}
                  aria-current={n.id === page ? "page" : undefined}
                  onClick={() => navigate(n.id)}
                >
                  <span className="nav-label">{n.label}</span>
                  {count !== undefined && <span className="nav-count mono">{count}</span>}
                </button>
              );
            })}
          </div>
        ))}
        {settings && (
          <div className="side-note mono">
            AUTOMATIC GRABBING<br />
            <b>{settings.automaticGrabbing ? "ON" : "OFF"}</b> — {settings.automaticGrabbing ? "matching releases can be grabbed automatically." : "nothing is acquired without your confirmation."}
          </div>
        )}
        <button className="nav signout" onClick={() => { setToken(null); setAuth("login"); }}>Sign out</button>
      </nav>
      <main className="content">
        {page === "dashboard" && <Dashboard />}
        {page === "movies" && <Movies onNavigate={navigate} />}
        {page === "series" && <Series onNavigate={navigate} />}
        {page === "wanted" && <Wanted />}
        {page === "activity" && <Activity initialTab={activityTab} />}
        {page === "settings" && <Settings initialSection={settingsSection} />}
        {page === "migration" && <Migration />}
      </main>
    </div>
  );
}

function AuthForm({ mode, onDone }: { mode: "setup" | "login"; onDone: () => void }) {
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const res = await api<{ token: string }>("POST", mode === "setup" ? "/api/auth/setup" : "/api/auth/login", { password });
      setToken(res.token);
      onDone();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="auth card" onSubmit={submit}>
      <h1>OneArr</h1>
      <p className="muted">
        {mode === "setup"
          ? "First run: choose an administrator password (12+ characters). It is stored as a salted PBKDF2 hash."
          : "Sign in to continue."}
      </p>
      <label>
        Password
        <input type="password" autoComplete={mode === "setup" ? "new-password" : "current-password"} value={password} onChange={(e) => setPassword(e.target.value)} required minLength={mode === "setup" ? 12 : 1} />
      </label>
      {error && <div className="error" role="alert">{error}</div>}
      <button type="submit" className="primary" disabled={busy}>{mode === "setup" ? "Create account" : "Sign in"}</button>
    </form>
  );
}
