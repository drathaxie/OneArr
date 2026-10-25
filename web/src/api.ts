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
  constructor(public status: number, message: string) {
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
    throw new ApiError(res.status, data?.error ?? `Request failed (${res.status})`);
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
  monitored: boolean;
  qualityProfileId: number;
  rootFolder: string;
  hasFile: boolean;
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
  releaseTitle: string;
  status: string;
  progress: number;
  clientState: string;
  category: string;
  hash: string | null;
  error: string | null;
  updatedAt: string;
}
