import { useCallback, useEffect, useRef, useState } from "react";
import { api, type MediaItem } from "./api";

export function availabilityClass(status: string | null | undefined): string {
  if (status === "Complete" || status === "Available") return "badge ok";
  if (status === "Missing") return "badge bad";
  if (status === "Partial") return "badge warn";
  return "badge";
}

/** Primary operational state, kept separate from physical availability on purpose.
 * An unmonitored title is never headlined Missing or Complete: monitoring is the
 * operational state, availability is secondary information (an unmonitored title with no
 * file is deliberately unwatched, not wanted). Monitored titles keep the availability
 * badge as primary, with Monitored alongside. Presentation only: the server's
 * availability, counts, and Wanted eligibility are untouched. Shared by the grid,
 * the inspector, and the detail view so they can never disagree. */
export function primaryStatus(
  item: Pick<MediaItem, "monitored" | "hasFile" | "availability" | "availableEpisodes" | "applicableEpisodes">,
  kind: "movie" | "series",
): { badge: string; badgeClass: string; secondary: string | null } {
  const avail = kind === "series" ? (item.availability ?? "Unknown") : item.hasFile ? "Available" : "Missing";
  if (item.monitored) return { badge: avail, badgeClass: availabilityClass(avail), secondary: null };
  if (kind === "series") {
    const secondary = item.applicableEpisodes != null && item.applicableEpisodes > 0
      ? `${avail} · ${item.availableEpisodes ?? 0} / ${item.applicableEpisodes} episodes available`
      : avail === "Unknown" ? "Episode data insufficient to classify" : avail;
    return { badge: "Unmonitored", badgeClass: "badge", secondary };
  }
  return { badge: "Unmonitored", badgeClass: "badge", secondary: item.hasFile ? "File available" : "No file" };
}

/** Availability stamp for poster cards and the dossier — one rule, shared by
 * both, so they can never disagree again. The stamp states the file fact and
 * nothing else; monitoring is always the separate mono note beside it.
 * - A file exists → "Available", monitored or not. Dune (monitored, file) and
 *   Spider-Man (unmonitored, file) mean the same thing and now say the same
 *   word; the note is what tells them apart.
 * - No file → "Missing" when monitored (the title is wanted) and "No file"
 *   when unmonitored (deliberately unwatched, never wanted). Those two still
 *   differ on purpose: wanted-ness is a real difference, not a wording drift.
 * - Series keep "Unmonitored" as the stamp when unmonitored: series
 *   availability is episode-based, and an unmonitored series must never be
 *   headlined Missing/Complete — its episode counts stay secondary text.
 * Presentation only: primaryStatus (detail/list) is untouched. */
export function availabilityStamp(
  item: Pick<MediaItem, "monitored" | "hasFile" | "availability" | "availableEpisodes" | "applicableEpisodes">,
  kind: "movie" | "series",
): { text: string; cls: string } {
  const status = primaryStatus(item, kind);
  if (item.monitored) return { text: status.badge, cls: status.badgeClass.replace("badge", "cc-stamp").trim() };
  if (kind === "movie") return item.hasFile ? { text: "Available", cls: "cc-stamp ok" } : { text: "No file", cls: "cc-stamp muted" };
  return { text: "Unmonitored", cls: "cc-stamp muted" };
}

/** Shared data-loading hook (previously defined in pages.tsx). */
export function useLoad<T>(path: string, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const reload = useCallback(() => {
    api<T>("GET", path)
      .then((d) => { setData(d); setError(null); })
      .catch((e: Error) => setError(e.message));
  }, [path]);
  useEffect(() => { reload(); }, [reload, ...deps]);
  return { data, error, reload };
}

export function ErrorLine({ message }: { message: string | null }) {
  return message ? <div className="error" role="alert">{message}</div> : null;
}

/** Accessible in-app confirmation dialog, replacing native confirm() for library removal.
 * Safety is preserved, not weakened: the destructive action only runs when the user
 * explicitly clicks the confirm button. Escape and backdrop clicks cancel. Focus starts
 * on Cancel so a stray Enter does not confirm a destructive action. */
export function ConfirmDialog({ title, body, confirmLabel, onConfirm, onCancel }: {
  title: string;
  body: string;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const cancelRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    cancelRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        onCancel();
      }
    };
    // Capture phase: this dialog is modal, so its Escape must win over page-level
    // shortcuts (inspector close, menu close) that would otherwise fire as well.
    document.addEventListener("keydown", onKey, true);
    return () => document.removeEventListener("keydown", onKey, true);
  }, [onCancel]);
  return (
    <div className="dialog-backdrop" role="presentation" onClick={(e) => { if (e.target === e.currentTarget) onCancel(); }}>
      <div className="dialog card cc-dialog" role="dialog" aria-modal="true" aria-label={title}>
        <h2 className="cc-dialog-title"><span className="cc-dialog-icon" aria-hidden="true">!</span>{title}</h2>
        <p>{body}</p>
        <div className="row" style={{ justifyContent: "flex-end" }}>
          <button ref={cancelRef} onClick={onCancel}>Cancel</button>
          <button className="danger primary-danger" onClick={onConfirm}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}
