// Thin client for the OneArr API. The bearer token lives in sessionStorage (cleared when the tab closes).
// No service credentials are ever handled here: qBittorrent, indexer and TMDb keys stay on the server.

const TOKEN_KEY = "onearr.token";

export function getToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function setToken(token: string | null): void {
  try {
    if (token) sessionStorage.setItem(TOKEN_KEY, token);
    else sessionStorage.removeItem(TOKEN_KEY);
  } catch {
    /* storage unavailable: user must sign in again on reload */
  }
}

export class ApiError extends Error {
  constructor(public status: number, message: string, public data?: unknown) {
    super(message);
  }
}

export async function api<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;

  const res = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!res.ok) {
    if (res.status === 401) setToken(null);
    throw new ApiError(res.status, data?.error ?? `Request failed (${res.status})`, data);
  }
  return data as T;
}

export type MediaKind = "movie" | "series";

export interface MediaItem {
  id: number;
  mediaType: "Movie" | "Series";
  providerId: string;
  title: string;
  year: number | null;
  overview: string | null;
  posterPath: string | null;
  monitored: boolean;
  qualityProfileId: number;
  rootFolder: string;
  hasFile: boolean;
  legacyPath?: string | null;
  legacySource?: string | null;
  /** Series: Complete/Partial/Missing/Unknown over monitored, aired episodes. Movies: Available/Missing. */
  availability?: string | null;
  /** Series only: monitored, aired episodes with a file / monitored, aired episodes in total. */
  availableEpisodes?: number | null;
  applicableEpisodes?: number | null;
}

export interface SettingsData {
  tmdbConfigured: boolean;
  dryRunImport: boolean;
  torrentRoot: string | null;
  mediaRoot: string | null;
  automaticGrabbing: boolean;
  defaultMovieRoot: string | null;
  defaultSeriesRoot: string | null;
  defaultMovieProfileId: number | null;
  defaultSeriesProfileId: number | null;
}

export function posterUrl(posterPath: string | null | undefined): string | null {
  if (!posterPath) return null;
  if (posterPath.startsWith("http")) return posterPath;
  return `https://image.tmdb.org/t/p/w342${posterPath}`;
}

export interface DetailSeason {
  seasonNumber: number;
  monitored: boolean;
  episodeCount: number;
  hasFileCount: number;
  wantedCount: number;
}

export interface DetailEpisode {
  id: number;
  seasonNumber: number;
  episodeNumber: number;
  title: string;
  airDate: string | null;
  monitored: boolean;
  hasFile: boolean;
  resolution: number | null;
}

export interface MediaDetail {
  id: number;
  mediaType: "Movie" | "Series";
  title: string;
  year: number | null;
  overview: string | null;
  posterPath: string | null;
  monitored: boolean;
  qualityProfileId: number;
  rootFolder: string;
  hasFile: boolean;
  legacyPath: string | null;
  legacySource: string | null;
  availability?: string | null;
  availableEpisodes?: number | null;
  applicableEpisodes?: number | null;
  seasons: DetailSeason[];
  episodes: DetailEpisode[];
  files: MediaFile[];
}

export interface WantedEpisode {
  mediaItemId: number;
  episodeId: number;
  seriesTitle: string;
  seasonNumber: number;
  episodeNumber: number;
  episodeTitle: string;
  airDate: string | null;
  hasActiveDownload: boolean;
}

export interface WantedList {
  movies: MediaItem[];
  episodes: WantedEpisode[];
  unmonitoredMoviesWithoutFile: number;
  unmonitoredEpisodesWithoutFile: number;
  movieHasActiveDownload: Record<string, boolean>;
}

export interface QualityProfile {
  id: number;
  name: string;
  minResolution: number;
  cutoffResolution: number;
  allowWebSources: boolean;
}

export interface MetadataResult {
  providerId: string;
  title: string;
  year: number | null;
  overview: string | null;
}

export interface MediaFile {
  path: string;
  sizeBytes: number;
  resolution: number | null;
  status: string;
  matchMethod: string;
}

export function formatBytes(sizeBytes: number): string {
  if (sizeBytes < 1024) return `${sizeBytes} B`;
  if (sizeBytes < 1024 * 1024) return `${(sizeBytes / 1024).toFixed(1)} KiB`;
  if (sizeBytes < 1024 * 1024 * 1024) return `${(sizeBytes / 1024 / 1024).toFixed(1)} MiB`;
  return `${(sizeBytes / 1024 / 1024 / 1024).toFixed(2)} GiB`;
}

export interface ConnectionTestResult {
  ok: boolean;
  service: string;
  name?: string;
  detail: string;
  categories?: { id: number; name: string }[];
  searchAvailable?: boolean;
  tvSearchAvailable?: boolean;
  movieSearchAvailable?: boolean;
  movieCategory?: string;
  movieCount?: number;
  tvCategory?: string;
  tvCount?: number;
  states?: { state: string; count: number }[];
  anyContentPath?: boolean;
  readableCount?: number;
  errorCount?: number;
  sampleTitles?: string[];
}

export interface EpisodeRow {
  id: number;
  seasonNumber: number;
  episodeNumber: number;
  title: string;
  airDate: string | null;
  monitored: boolean;
  hasFile: boolean;
}

export interface ReleaseResult {
  title: string;
  link: string;
  infoHash: string | null;
  size: number;
  seeders: number;
  indexer: string;
  resolution: number | null;
  source: string;
  accepted: boolean;
  score: number;
  reasons: string[];
}

export interface Job {
  id: number;
  mediaItemId: number | null;
  episodeId: number | null;
  mediaType: string;
  releaseTitle: string;
  status: string;
  progress: number;
  clientState: string;
  category: string;
  downloadClientId: number | null;
  clientName: string;
  downloadSpeedBps: number;
  etaSeconds: number;
  contentPath: string | null;
  hash: string | null;
  error: string | null;
  importFailed: boolean;
  importReady: boolean;
  lastSeenAt: string | null;
  completedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface QueueClient {
  id: number;
  name: string;
  enabled: boolean;
  lastSeenAt: string | null;
  lastError: string | null;
}

export interface QueueData {
  jobs: Job[];
  clients: QueueClient[];
}

export interface HistoryEntry {
  at: string;
  kind: string;
  title: string;
  jobId: number | null;
}

export interface Problem {
  kind: string;
  title: string;
  detail: string;
  jobId: number | null;
  clientId: number | null;
  actions: string[];
}

export interface ImportPlan {
  sourcePath: string;
  destinationPath: string | null;
  sizeBytes: number;
  lastWriteUtcTicks: number;
  mediaType: string;
  plannedAction: string;
  reason: string;
  destinationConflict: boolean;
  sameVolume: boolean;
  matchedEpisodeIds: number[];
  matchedEpisodeLabels: string[];
}

export interface ImportPreview {
  previewId: number;
  jobId: number;
  createdAt: string;
  plans: ImportPlan[];
  fingerprint: string;
}
