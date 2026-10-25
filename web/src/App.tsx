import { useEffect, useState } from "react";
import { api, getToken, setToken } from "./api";
import { Dashboard, Movies, Series, Wanted, Queue, Settings, Migration } from "./pages";

type Page = "dashboard" | "movies" | "series" | "wanted" | "queue" | "settings" | "migration";

const NAV: { id: Page; label: string }[] = [
  { id: "dashboard", label: "Dashboard" },
  { id: "movies", label: "Movies" },
  { id: "series", label: "Series" },
  { id: "wanted", label: "Wanted" },
  { id: "queue", label: "Queue" },
  { id: "settings", label: "Settings" },
  { id: "migration", label: "Migration Preview" },
];

export function App() {
  const [auth, setAuth] = useState<"loading" | "setup" | "login" | "in">("loading");
  const [page, setPage] = useState<Page>("dashboard");

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
        <div className="brand">OneArr</div>
        {NAV.map((n) => (
          <button key={n.id} className={n.id === page ? "nav active" : "nav"} onClick={() => setPage(n.id)}>
            {n.label}
          </button>
        ))}
        <button className="nav signout" onClick={() => { setToken(null); setAuth("login"); }}>Sign out</button>
      </nav>
      <main className="content">
        {page === "dashboard" && <Dashboard />}
        {page === "movies" && <Movies />}
        {page === "series" && <Series />}
        {page === "wanted" && <Wanted />}
        {page === "queue" && <Queue />}
        {page === "settings" && <Settings />}
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
