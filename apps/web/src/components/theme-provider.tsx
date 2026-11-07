"use client";

import * as React from "react";
import { ThemeProvider as NextThemesProvider } from "next-themes";

// Derived from the component, not imported from "next-themes/dist/types":
// that path is gone in next-themes 0.4, which React 19 needs.
type ThemeProviderProps = React.ComponentProps<typeof NextThemesProvider>;

export function ThemeProvider({ children, ...props }: ThemeProviderProps) {
  return <NextThemesProvider {...props}>{children}</NextThemesProvider>;
}
