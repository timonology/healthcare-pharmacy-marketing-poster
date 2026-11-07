"use client";

import { useEffect } from "react";
import { create } from "zustand";
import { api } from "@/lib/api";
import { tokenValuesFrom, type TokenValues } from "@/lib/canvas/display-tokens";
import { useAuthStore } from "./auth-store";

/**
 * The pharmacy's details for drawing `{{tokens}}` on screen — the editor, and
 * the poster thumbnails.
 *
 * Null until loaded, which draws every token as a blank — never the raw
 * `{{address}}`. Loaded once per session from the profile; the editor
 * refreshes it when it opens, so details added during onboarding show at once.
 */
interface TokenValuesState {
  values: TokenValues | null;
  loading: boolean;
  setValues: (values: TokenValues | null) => void;
}

export const useTokenValuesStore = create<TokenValuesState>()((set) => ({
  values: null,
  loading: false,
  setValues: (values) => set({ values, loading: false }),
}));

// Another person signing in on this browser must never see the last
// pharmacy's details drawn into their posters.
useAuthStore.subscribe((state, prev) => {
  if (state.user?.id !== prev.user?.id) useTokenValuesStore.setState({ values: null, loading: false });
});

/** The values, fetching them once if nobody has yet. */
export function usePharmacyTokenValues(): TokenValues | null {
  const values = useTokenValuesStore((s) => s.values);
  useEffect(() => {
    const state = useTokenValuesStore.getState();
    if (state.values || state.loading) return;
    useTokenValuesStore.setState({ loading: true });
    api.getMe()
      .then((me) => useTokenValuesStore.getState().setValues(tokenValuesFrom(me.profile)))
      // Blanks rather than raw tokens if the profile cannot be read.
      .catch(() => useTokenValuesStore.getState().setValues({}));
  }, []);
  return values;
}
