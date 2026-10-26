import { useState } from "react";
import { api, type ConnectionTestResult, type QualityProfile, type SettingsData } from "./api";
import { ErrorLine, useLoad } from "./common";
import { LibraryPanel, RootFolderTool } from "./library";
import { Migration } from "./migration";

// Settings, reorganised Sonarr/Radarr-style: an overview, a persistent sidebar with expandable
// categories, and one section at a time. Only features OneArr actually supports are shown — nothing
// here is decorative, and nothing implies an unimplemented Sonarr feature.

type SectionId = "overview" | "media" | "profiles" | "indexers" | "clients" | "metadata" | "automation" | "migration" | "general" | "ui";

interface Subsection { id: string; label: string; advanced?: boolean }
interface Category { id: SectionId; label: string; description: string; subsections: Subsection[] }

const CATEGORIES: Category[] = [
  { id: "media", label: "Media Management", description: "Root folders, default folders per media type, library folders, path mappings, bulk root-folder changes, and library scanning with scan review.", subsections: [
    { id: "media-defaults", label: "Default root folders" },
    { id: "media-paths", label: "Import paths" },
    { id: "media-bulk", label: "Bulk root folders" },
    { id: "media-library", label: "Library folders & scan" },
    { id: "media-mappings", label: "Path mappings", advanced: true },
  ] },
  { id: "profiles", label: "Profiles", description: "Quality profiles and the default profile used for new movies and series.", subsections: [
    { id: "profiles-list", label: "Quality profiles" },
    { id: "profiles-defaults", label: "Default profiles" },
  ] },
  { id: "indexers", label: "Indexers", description: "Torznab / Newznab indexers and their connection tests.", subsections: [
    { id: "indexers-list", label: "Indexers" },
    { id: "indexers-tests", label: "Connection tests", advanced: true },
  ] },
  { id: "clients", label: "Download Clients", description: "qBittorrent clients, their categories, and connection tests.", subsections: [
    { id: "clients-list", label: "Download clients" },
    { id: "clients-tests", label: "Connection tests", advanced: true },
  ] },
  { id: "metadata", label: "Metadata", description: "TMDb configuration and its connection test.", subsections: [
    { id: "metadata-tmdb", label: "TMDb" },
    { id: "metadata-tests", label: "Connection tests", advanced: true },
  ] },
  { id: "automation", label: "Automation", description: "Automatic grabbing toggle and the library safety gate that must pass before any automatic search runs.", subsections: [
    { id: "automation-status", label: "Automation status" },
    { id: "automation-toggle", label: "Automatic grabbing" },
  ] },
  { id: "migration", label: "Migration", description: "Sonarr/Radarr import previews and monitoring synchronisation, with the preview-required Apply safety mechanism.", subsections: [
    { id: "migration-import", label: "Import preview" },
    { id: "migration-sync", label: "Monitoring sync" },
  ] },
  { id: "general", label: "General", description: "Application and server settings, including security.", subsections: [
    { id: "general-app", label: "Application" },
    { id: "general-security", label: "Security" },
  ] },
  { id: "ui", label: "UI", description: "Display preferences: the default Posters/List view and the advanced-settings toggle.", subsections: [
    { id: "ui-view", label: "Default view" },
    { id: "ui-theme", label: "Theme" },
  ] },
];

function scrollToSubsection(id: string) {
  const el = document.getElementById(id);
  if (el && typeof (el as HTMLElement).scrollIntoView === "function") (el as HTMLElement).scrollIntoView();
}

export function Settings() {
  const [section, setSection] = useState<SectionId>("overview");
  const [expanded, setExpanded] = useState<Set<SectionId>>(new Set(["media"]));
  const [advanced, setAdvanced] = useState<boolean>(() => {
    try { return localStorage.getItem("onearr.advanced") !== "false"; } catch { return true; }
  });

  function setAdvancedMode(v: boolean) {
    setAdvanced(v);
    try { localStorage.setItem("onearr.advanced", String(v)); } catch { /* best-effort */ }
  }

  function openSection(id: SectionId) {
    setSection(id);
    setExpanded((prev) => new Set(prev).add(id));
  }

  function toggleExpanded(id: SectionId) {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  return (
    <div className="settings-shell">
      <aside className="settings-sidebar" aria-label="Settings">
        <button className={section === "overview" ? "nav active" : "nav"} aria-current={section === "overview" ? "page" : undefined} onClick={() => setSection("overview")}>Settings overview</button>
        {CATEGORIES.map((cat) => (
          <div key={cat.id}>
            <div className="row" style={{ gap: ".25rem" }}>
              <button className={section === cat.id ? "nav active" : "nav"} style={{ flex: 1 }} aria-current={section === cat.id ? "page" : undefined} aria-expanded={expanded.has(cat.id)} onClick={() => openSection(cat.id)}>{cat.label}</button>
              <button className="small" aria-label={`${expanded.has(cat.id) ? "Collapse" : "Expand"} ${cat.label}`} onClick={() => toggleExpanded(cat.id)}>{expanded.has(cat.id) ? "▾" : "▸"}</button>
            </div>
            {expanded.has(cat.id) && cat.subsections.filter((s) => advanced || !s.advanced).map((sub) => (
              <button key={sub.id} className="nav sub" onClick={() => { openSection(cat.id); setTimeout(() => scrollToSubsection(sub.id), 0); }}>{sub.label}</button>
            ))}
          </div>
        ))}
        <button className="nav" aria-pressed={advanced} onClick={() => setAdvancedMode(!advanced)}>{advanced ? "Hide Advanced" : "Show Advanced"}</button>
        {!advanced && <p className="muted">Advanced items (path mappings, connection tests) are hidden.</p>}
      </aside>
      <div className="settings-content">
        {section === "overview" && <Overview onOpen={openSection} advanced={advanced} />}
        {section === "media" && <MediaSection advanced={advanced} />}
        {section === "profiles" && <ProfilesSection />}
        {section === "indexers" && <IndexersSection advanced={advanced} />}
        {section === "clients" && <ClientsSection advanced={advanced} />}
        {section === "metadata" && <MetadataSection advanced={advanced} />}
        {section === "automation" && <AutomationSection />}
        {section === "migration" && <div id="migration-import"><Migration /></div>}
        {section === "general" && <GeneralSection />}
        {section === "ui" && <UiSection advanced={advanced} onAdvanced={setAdvancedMode} />}
      </div>
    </div>
  );
}

function Overview({ onOpen, advanced }: { onOpen: (id: SectionId) => void; advanced: boolean }) {
  return (
    <>
      <h2>Settings</h2>
      <p className="muted">OneArr's configuration, organised like Sonarr and Radarr. Pick a category to change it; the poster library itself is unchanged.</p>
      {!advanced && <p className="muted">Advanced items (path mappings, connection tests) are hidden. Use Show Advanced in the sidebar to reveal them.</p>}
      <div className="grid">
        {CATEGORIES.map((cat) => (
          <div key={cat.id} className="card" style={{ marginBottom: 0 }}>
            <h2>{cat.label}</h2>
            <p className="muted">{cat.description}</p>
            <button className="primary" onClick={() => onOpen(cat.id)}>Open {cat.label}</button>
          </div>
        ))}
      </div>
    </>
  );
}

async function saveSettings(body: Record<string, unknown>, setMessage: (m: string | null) => void, reload?: () => void) {
  setMessage(null);
  try {
    await api("PUT", "/api/settings", body);
    setMessage("Saved");
    reload?.();
  } catch (e) {
    setMessage((e as Error).message);
  }
}

function MediaSection({ advanced }: { advanced: boolean }) {
  const { data, error, reload } = useLoad<SettingsData>("/api/settings");
  return (
    <>
      <h2>Media Management</h2>
      <ErrorLine message={error} />
      {data && (
        <>
          <div id="media-defaults"><DefaultRootsForm data={data} onSaved={reload} /></div>
          <div id="media-paths"><MediaPathsForm data={data} onSaved={reload} /></div>
        </>
      )}
      <div id="media-bulk" className="card">
        <h2>Bulk root folders</h2>
        <p className="muted">Change the root folder for many titles at once, per media type. Only where future downloads go changes; existing files are not moved.</p>
        <h3>Movies</h3>
        <RootFolderTool kind="movie" onChanged={() => undefined} />
        <h3>Series</h3>
        <RootFolderTool kind="series" onChanged={() => undefined} />
      </div>
      <div id="media-library"><LibraryPanel showMappings={advanced} /></div>
      {!advanced && <p className="muted" id="media-mappings">Path mappings are an advanced item. Use Show Advanced in the sidebar to reveal them.</p>}
    </>
  );
}

function DefaultRootsForm({ data, onSaved }: { data: SettingsData; onSaved: () => void }) {
  const [movieRoot, setMovieRoot] = useState(data.defaultMovieRoot ?? "");
  const [seriesRoot, setSeriesRoot] = useState(data.defaultSeriesRoot ?? "");
  const [message, setMessage] = useState<string | null>(null);
  return (
    <form className="card" onSubmit={(e) => { e.preventDefault(); void saveSettings({ defaultMovieRoot: movieRoot, defaultSeriesRoot: seriesRoot }, setMessage, onSaved); }}>
      <h2>Default root folders</h2>
      <p className="muted">
        The Add Movie and Add Series dialogs start from these folders, separately per media type — the movie
        default can never become the series default. Leave one blank to fall back to the media root below for
        that type. Changing a default never rewrites any title already in OneArr.
      </p>
      <label>Default movie root folder
        <input aria-label="Default movie root folder" value={movieRoot} onChange={(e) => setMovieRoot(e.target.value)} placeholder="D:\Media\Movies" />
      </label>
      <label>Default series root folder
        <input aria-label="Default series root folder" value={seriesRoot} onChange={(e) => setSeriesRoot(e.target.value)} placeholder="D:\Media\TV" />
      </label>
      <ErrorLine message={message} />
      <button className="primary" type="submit">Save defaults</button>
    </form>
  );
}

function MediaPathsForm({ data, onSaved }: { data: SettingsData; onSaved: () => void }) {
  const [torrentRoot, setTorrentRoot] = useState(data.torrentRoot ?? "");
  const [mediaRoot, setMediaRoot] = useState(data.mediaRoot ?? "");
  const [dry, setDry] = useState<boolean>(data.dryRunImport);
  const [message, setMessage] = useState<string | null>(null);
  return (
    <form className="card" onSubmit={(e) => { e.preventDefault(); void saveSettings({ torrentRoot, mediaRoot, dryRunImport: dry }, setMessage, onSaved); }}>
      <h2>Import paths</h2>
      <p className="muted">Where downloads live, the fallback media root used when a per-type default is blank, and whether imports are dry-run only.</p>
      <label>Torrent download root (absolute)
        <input aria-label="Torrent download root" value={torrentRoot} onChange={(e) => setTorrentRoot(e.target.value)} placeholder="D:\Downloads\Torrents" />
      </label>
      <label>Media root (fallback)
        <input aria-label="Media root (fallback)" value={mediaRoot} onChange={(e) => setMediaRoot(e.target.value)} placeholder="D:\Media" />
      </label>
      <label className="inline"><input type="checkbox" checked={dry} onChange={(e) => setDry(e.target.checked)} /> Imports are dry-run only (recommended until verified)</label>
      <ErrorLine message={message} />
      <button className="primary" type="submit">Save import paths</button>
    </form>
  );
}

function ProfilesSection() {
  const { data: profiles, reload: reloadProfiles } = useLoad<QualityProfile[]>("/api/quality-profiles");
  const { data: settings, reload: reloadSettings } = useLoad<SettingsData>("/api/settings");
  return (
    <>
      <h2>Profiles</h2>
      <div className="card" id="profiles-list">
        <h2>Quality profiles</h2>
        <ul>{profiles?.map((p) => <li key={p.id}>{p.name}: minimum {p.minResolution}p, cutoff {p.cutoffResolution}p {p.allowWebSources ? "" : "(no web sources)"}</li>)}</ul>
        <ProfileForm onCreated={reloadProfiles} />
      </div>
      {settings && profiles && (
        <div id="profiles-defaults"><DefaultProfilesForm data={settings} profiles={profiles} onSaved={reloadSettings} /></div>
      )}
    </>
  );
}

function DefaultProfilesForm({ data, profiles, onSaved }: { data: SettingsData; profiles: QualityProfile[]; onSaved: () => void }) {
  const [movieProfile, setMovieProfile] = useState<number>(data.defaultMovieProfileId ?? profiles[0]?.id ?? 0);
  const [seriesProfile, setSeriesProfile] = useState<number>(data.defaultSeriesProfileId ?? profiles[0]?.id ?? 0);
  const [message, setMessage] = useState<string | null>(null);
  return (
    <form className="card" onSubmit={(e) => { e.preventDefault(); void saveSettings({ defaultMovieProfileId: movieProfile, defaultSeriesProfileId: seriesProfile }, setMessage, onSaved); }}>
      <h2>Default profiles</h2>
      <p className="muted">The Add dialogs start from these profiles, separately for movies and series. Changing a default never rewrites any title already in OneArr.</p>
      <label>Default movie profile
        <select aria-label="Default movie profile" value={movieProfile} onChange={(e) => setMovieProfile(Number(e.target.value))}>
          {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </label>
      <label>Default series profile
        <select aria-label="Default series profile" value={seriesProfile} onChange={(e) => setSeriesProfile(Number(e.target.value))}>
          {profiles.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </label>
      <ErrorLine message={message} />
      <button className="primary" type="submit">Save default profiles</button>
    </form>
  );
}

function ProfileForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("");
  const [min, setMin] = useState(720);
  const [cutoff, setCutoff] = useState(1080);
  const [web, setWeb] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  return (
    <form className="row" onSubmit={async (e) => {
      e.preventDefault(); setErr(null);
      try { await api("POST", "/api/quality-profiles", { name, minResolution: min, cutoffResolution: cutoff, allowWebSources: web }); setName(""); onCreated(); }
      catch (x) { setErr((x as Error).message); }
    }}>
      <input aria-label="Profile name" placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
      <select aria-label="Minimum resolution" value={min} onChange={(e) => setMin(Number(e.target.value))}>{[480, 720, 1080, 2160].map((r) => <option key={r} value={r}>min {r}p</option>)}</select>
      <select aria-label="Cutoff resolution" value={cutoff} onChange={(e) => setCutoff(Number(e.target.value))}>{[480, 720, 1080, 2160].map((r) => <option key={r} value={r}>cutoff {r}p</option>)}</select>
      <label className="inline"><input type="checkbox" checked={web} onChange={(e) => setWeb(e.target.checked)} /> allow web sources</label>
      <button type="submit">Add profile</button>
      <ErrorLine message={err} />
    </form>
  );
}

function IndexersSection({ advanced }: { advanced: boolean }) {
  const { data: indexers, reload: reloadIndexers } = useLoad<any[]>("/api/indexers");
  return (
    <>
      <h2>Indexers</h2>
      <div className="card" id="indexers-list">
        <h2>Indexers (Torznab / Newznab)</h2>
        <p className="muted">Keys are stored server-side and never shown again.</p>
        <ul>{indexers?.map((i) => <li key={i.id} className="row spread">{i.name} <span className="muted">{i.baseUrl}</span> <button className="small danger" onClick={async () => { await api("DELETE", `/api/indexers/${i.id}`); reloadIndexers(); }}>Delete</button></li>)}</ul>
        <IndexerForm onCreated={reloadIndexers} />
      </div>
      <div id="indexers-tests">{advanced ? <ConnectionTests scope="indexers" indexers={indexers ?? []} clients={[]} /> : <p className="muted">Connection tests are an advanced item. Use Show Advanced in the sidebar to reveal them.</p>}</div>
    </>
  );
}

function IndexerForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [key, setKey] = useState("");
  const [err, setErr] = useState<string | null>(null);
  return (
    <form className="row" onSubmit={async (e) => {
      e.preventDefault(); setErr(null);
      try { await api("POST", "/api/indexers", { name, baseUrl: url, apiKey: key, enabled: true }); setName(""); setUrl(""); setKey(""); onCreated(); }
      catch (x) { setErr((x as Error).message); }
    }}>
      <input aria-label="Indexer name" placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
      <input aria-label="Indexer URL" placeholder="https://indexer.example/api/…torznab" value={url} onChange={(e) => setUrl(e.target.value)} required style={{ minWidth: 260 }} />
      <input aria-label="Indexer API key" type="password" autoComplete="off" placeholder="API key" value={key} onChange={(e) => setKey(e.target.value)} />
      <button type="submit">Add indexer</button>
      <ErrorLine message={err} />
    </form>
  );
}

function ClientsSection({ advanced }: { advanced: boolean }) {
  const { data: clients, reload: reloadClients } = useLoad<any[]>("/api/download-clients");
  return (
    <>
      <h2>Download Clients</h2>
      <div className="card" id="clients-list">
        <h2>Download clients (qBittorrent)</h2>
        <p className="muted">Credentials are stored server-side and never returned to the browser.</p>
        <ul>{clients?.map((c) => <li key={c.id} className="row spread">{c.name} <span className="muted">{c.baseUrl} · {c.movieCategory} / {c.tvCategory}</span> <button className="small danger" onClick={async () => { await api("DELETE", `/api/download-clients/${c.id}`); reloadClients(); }}>Delete</button></li>)}</ul>
        <ClientForm onCreated={reloadClients} />
      </div>
      <div id="clients-tests">{advanced ? <ConnectionTests scope="clients" indexers={[]} clients={clients ?? []} /> : <p className="muted">Connection tests are an advanced item. Use Show Advanced in the sidebar to reveal them.</p>}</div>
    </>
  );
}

function ClientForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("qBittorrent");
  const [url, setUrl] = useState("");
  const [user, setUser] = useState("");
  const [pass, setPass] = useState("");
  const [movie, setMovie] = useState("onearr-movies");
  const [tv, setTv] = useState("onearr-tv");
  const [err, setErr] = useState<string | null>(null);
  return (
    <form className="row" onSubmit={async (e) => {
      e.preventDefault(); setErr(null);
      try { await api("POST", "/api/download-clients", { name, baseUrl: url, username: user, password: pass, movieCategory: movie, tvCategory: tv, enabled: true }); setPass(""); onCreated(); }
      catch (x) { setErr((x as Error).message); }
    }}>
      <input aria-label="Client name" value={name} onChange={(e) => setName(e.target.value)} required />
      <input aria-label="Client URL" placeholder="http://qbittorrent:8080" value={url} onChange={(e) => setUrl(e.target.value)} required style={{ minWidth: 220 }} />
      <input aria-label="Client username" value={user} onChange={(e) => setUser(e.target.value)} />
      <input aria-label="Client password" type="password" autoComplete="off" value={pass} onChange={(e) => setPass(e.target.value)} />
      <input aria-label="Movie category" value={movie} onChange={(e) => setMovie(e.target.value)} />
      <input aria-label="TV category" value={tv} onChange={(e) => setTv(e.target.value)} />
      <button type="submit">Add client</button>
      <ErrorLine message={err} />
    </form>
  );
}

function MetadataSection({ advanced }: { advanced: boolean }) {
  const { data, error, reload } = useLoad<SettingsData>("/api/settings");
  return (
    <>
      <h2>Metadata</h2>
      <ErrorLine message={error} />
      {data && (
        <div id="metadata-tmdb"><MetadataForm data={data} onSaved={reload} /></div>
      )}
      <div id="metadata-tests">{advanced ? <ConnectionTests scope="metadata" indexers={[]} clients={[]} /> : <p className="muted">Connection tests are an advanced item. Use Show Advanced in the sidebar to reveal them.</p>}</div>
    </>
  );
}

function MetadataForm({ data, onSaved }: { data: SettingsData; onSaved: () => void }) {
  const [tmdb, setTmdb] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  return (
    <form className="card" onSubmit={(e) => { e.preventDefault(); void saveSettings({ tmdbApiKey: tmdb || undefined }, setMessage, onSaved); setTmdb(""); }}>
      <h2>TMDb</h2>
      <p className="muted">Search and adding titles need a free TMDb API key. The key is stored server-side and never shown back.</p>
      <label>TMDb API key {data.tmdbConfigured ? <span className="ok">configured</span> : <span className="error">not set</span>}
        <input type="password" autoComplete="off" value={tmdb} onChange={(e) => setTmdb(e.target.value)} placeholder={data.tmdbConfigured ? "Leave blank to keep the stored key" : "Paste your TMDb v3 key or v4 read token"} />
      </label>
      <ErrorLine message={message} />
      <button className="primary" type="submit">Save TMDb key</button>
    </form>
  );
}

function AutomationSection() {
  const { data, error, reload } = useLoad<SettingsData>("/api/settings");
  return (
    <>
      <h2>Automation</h2>
      <ErrorLine message={error} />
      <div id="automation-status"><AutomationStatus /></div>
      {data && (
        <div id="automation-toggle"><AutomationForm data={data} onSaved={reload} /></div>
      )}
    </>
  );
}

function AutomationStatus() {
  const { data: settings } = useLoad<SettingsData>("/api/settings");
  const { data: status } = useLoad<any>("/api/library/status");
  if (!settings || !status) return null;
  const on = settings.automaticGrabbing === true;
  const gateOk = status.automation?.allowed === true;
  return (
    <div className="card">
      <h2>Automation</h2>
      <p className={on && gateOk ? "ok" : "error"}>
        Automatic grabbing is {on ? "ON" : "OFF"}. {gateOk ? "Library reconciled." : `Blocked: ${status.automation?.reason ?? "library not reconciled"}`}
      </p>
      <p className="muted">
        {on && !gateOk
          ? "The toggle is on, but automatic searches are still paused until the library check passes. That is the safety gate, not a bug: apply a complete library scan in the Library section below, and make sure every folder is reachable and every unmatched file is reviewed."
          : !on
            ? "Automatic grabbing stays off until you turn it on here. Even then, the library gate above must pass before any automatic search runs."
            : "Automatic searches may run for monitored wanted items with no file and no active or missing download."}
      </p>
    </div>
  );
}

function AutomationForm({ data, onSaved }: { data: SettingsData; onSaved: () => void }) {
  const [auto, setAuto] = useState<boolean>(data.automaticGrabbing ?? false);
  const [message, setMessage] = useState<string | null>(null);
  return (
    <form className="card" onSubmit={(e) => { e.preventDefault(); void saveSettings({ automaticGrabbing: auto }, setMessage, onSaved); }}>
      <h2>Automatic grabbing</h2>
      <label className="inline"><input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} /> Automatic grabbing (off by default)</label>
      <p className="muted">Turning this on does not bypass the library check: automatic searches only run when the library is reconciled, every folder is reachable, and no download is already active for an item. Until then, all grabs are manual. Keep it off during testing unless you have applied a complete library scan.</p>
      <ErrorLine message={message} />
      <button className="primary" type="submit">Save automation</button>
    </form>
  );
}

function GeneralSection() {
  const { data: auth } = useLoad<{ configured: boolean; authenticated: boolean }>("/api/auth/status");
  return (
    <>
      <h2>General</h2>
      <div className="card" id="general-app">
        <h2>Application</h2>
        <p className="muted">OneArr is a single-process application with a local SQLite database. There are no other server-wide settings to change here; application behaviour lives in Media Management, Automation, and the other categories.</p>
      </div>
      <div className="card" id="general-security">
        <h2>Security</h2>
        {auth && (
          <p className={auth.configured ? "ok" : "error"}>
            Administrator password: {auth.configured ? "set" : "not set"}. {auth.authenticated ? "You are signed in." : "You are not signed in."}
          </p>
        )}
        <p className="muted">The administrator password is stored as a salted PBKDF2 hash. API keys and download-client passwords are stored server-side and are never returned to the browser. There is no password-change screen yet; changing the administrator password is not currently supported from this UI.</p>
      </div>
    </>
  );
}

function UiSection({ advanced, onAdvanced }: { advanced: boolean; onAdvanced: (v: boolean) => void }) {
  const [view, setView] = useState<"grid" | "list">(() => {
    try { return (localStorage.getItem("onearr.view") as "grid" | "list") || "grid"; } catch { return "grid"; }
  });
  function setViewMode(v: "grid" | "list") {
    setView(v);
    try { localStorage.setItem("onearr.view", v); } catch { /* best-effort */ }
  }
  return (
    <>
      <h2>UI</h2>
      <div className="card" id="ui-view">
        <h2>Default view</h2>
        <p className="muted">The Movies and Series pages remember this choice in your browser. Posters is the default.</p>
        <div className="row">
          <label className="inline"><input type="radio" name="ui-view" checked={view === "grid"} onChange={() => setViewMode("grid")} /> Posters</label>
          <label className="inline"><input type="radio" name="ui-view" checked={view === "list"} onChange={() => setViewMode("list")} /> List</label>
        </div>
      </div>
      <div className="card" id="ui-theme">
        <h2>Theme</h2>
        <p className="muted">OneArr follows your system theme: dark when your system is dark, light otherwise. There is no separate light/dark setting.</p>
      </div>
      <div className="card">
        <h2>Advanced settings</h2>
        <p className="muted">Advanced items are path mappings and connection tests. They are currently {advanced ? "shown" : "hidden"}.</p>
        <button onClick={() => onAdvanced(!advanced)}>{advanced ? "Hide Advanced" : "Show Advanced"}</button>
      </div>
    </>
  );
}

function ConnectionTests({ scope, indexers, clients }: { scope: "all" | "metadata" | "indexers" | "clients"; indexers: any[]; clients: any[] }) {
  const [result, setResult] = useState<ConnectionTestResult | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  async function test(label: string, body: Record<string, unknown>) {
    setBusy(label); setMessage(null);
    try {
      setResult(await api<ConnectionTestResult>("POST", "/api/connections/test", body));
    } catch (e) {
      setMessage((e as Error).message);
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="card">
      <h2>Connection tests</h2>
      <p className="muted">Read-only checks. Nothing is downloaded, imported, or written to another application, and no key or password is shown back.</p>
      <div className="row">
        {(scope === "all" || scope === "metadata") && <button disabled={busy !== null} onClick={() => test("tmdb", { service: "tmdb" })}>{busy === "tmdb" ? "Testing…" : "Test TMDb"}</button>}
        {(scope === "all" || scope === "indexers") && indexers.map((i: any) => (
          <button key={i.id} disabled={busy !== null} onClick={() => test(`indexer-${i.id}`, { service: "torznab", indexerId: i.id })}>Test indexer: {i.name}</button>
        ))}
        {(scope === "all" || scope === "clients") && clients.map((c: any) => (
          <button key={c.id} disabled={busy !== null} onClick={() => test(`client-${c.id}`, { service: "qbittorrent", clientId: c.id })}>Test qBittorrent: {c.name}</button>
        ))}
      </div>
      <ErrorLine message={message} />
      {result && (
        <div>
          <p className={result.ok ? "ok" : "error"}>{result.detail}</p>
          {result.categories && result.categories.length > 0 && (
            <p className="muted">Categories: {result.categories.slice(0, 20).map((c) => `${c.id} ${c.name}`).join(", ")}
              {result.categories.length > 20 ? ` (+${result.categories.length - 20} more)` : ""} · search: {result.searchAvailable ? "yes" : "no"}, TV search: {result.tvSearchAvailable ? "yes" : "no"}, movie search: {result.movieSearchAvailable ? "yes" : "no"}</p>
          )}
          {result.states && <p className="muted">Torrent states: {result.states.map((s) => `${s.state}: ${s.count}`).join(", ")} · content paths reported: {result.anyContentPath ? "yes" : "no"}</p>}
        </div>
      )}
    </div>
  );
}
