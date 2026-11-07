"use client";

import { useCallback, useEffect, useState } from "react";
import {
  DEFAULT_WINDOW,
  parseListWindow,
  writeListWindow,
  type ListWindow,
} from "@/lib/list-window";

/**
 * A list's sort and date range, kept in the address bar.
 *
 * Read from `window.location` after mount rather than with `useSearchParams`,
 * which would force every list page into a Suspense boundary for static
 * rendering (see admin/templates/new for the same choice). The cost is one
 * render with the defaults, so pages wait for `ready` before they fetch —
 * otherwise a shared link would load the unfiltered list first and then the
 * filtered one, and whichever answered last would win.
 *
 * Written with `history.replaceState`: changing a filter should not add a
 * Back step per click, but a refresh or a copied link keeps it. Other query
 * parameters on the page are left alone. Back and Forward between pages are
 * honoured through `popstate`.
 */
export function useListWindow() {
  const [listWindow, setState] = useState<ListWindow>(DEFAULT_WINDOW);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    const read = () => setState(parseListWindow(window.location.search));
    read();
    setReady(true);
    window.addEventListener("popstate", read);
    return () => window.removeEventListener("popstate", read);
  }, []);

  const setListWindow = useCallback((next: ListWindow) => {
    setState(next);
    const url = new URL(window.location.href);
    const params = writeListWindow(url.searchParams, next);
    const query = params.toString();
    window.history.replaceState(
      window.history.state,
      "",
      `${url.pathname}${query ? `?${query}` : ""}${url.hash}`,
    );
  }, []);

  /** Back to all time. The sort is a preference, not a filter, so it stays. */
  const clearDates = useCallback(
    () => setListWindow({ ...listWindow, from: null, to: null }),
    [listWindow, setListWindow],
  );

  return { listWindow, setListWindow, clearDates, ready };
}
