"use client";

import { create } from "zustand";
import type { CurrentSubscription, SubscriptionTier } from "@acme/shared-types";

interface SubscriptionState {
  current: CurrentSubscription | null;
  loading: boolean;
  error: string | null;
  setCurrent: (s: CurrentSubscription | null) => void;
  setLoading: (b: boolean) => void;
  setError: (e: string | null) => void;
}

export const useSubscriptionStore = create<SubscriptionState>()((set) => ({
  current: null,
  loading: false,
  error: null,
  setCurrent: (current) => set({ current, error: null }),
  setLoading: (loading) => set({ loading }),
  setError: (error) => set({ error }),
}));

export function tierLabel(tier: SubscriptionTier): string {
  switch (tier) {
    case "Free": return "Free";
    case "Starter": return "Starter";
    case "Pro": return "Pro";
  }
}
