"use client";

import { create } from "zustand";
import type { UserProfile } from "@acme/shared-types";

/**
 * Auth store — *no token* lives here. Tokens are kept in httpOnly cookies
 * managed by the Next.js BFF. The client only needs to know who's signed in.
 */
interface AuthState {
  user: UserProfile | null;
  setUser: (user: UserProfile | null) => void;
  clear: () => void;
}

export const useAuthStore = create<AuthState>()((set) => ({
  user: null,
  setUser: (user) => set({ user }),
  clear: () => set({ user: null }),
}));
