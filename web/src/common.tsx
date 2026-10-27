import { useCallback, useEffect, useState } from "react";
import { api } from "./api";

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
