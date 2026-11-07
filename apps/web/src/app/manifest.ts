import type { MetadataRoute } from "next";
import { SITE_DESCRIPTION, SITE_NAME } from "@/lib/site";

/** Lets a pharmacy pin the app to a tablet's home screen with the right name and colours. */
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: SITE_NAME,
    short_name: SITE_NAME,
    description: SITE_DESCRIPTION,
    start_url: "/dashboard",
    display: "standalone",
    background_color: "#ffffff",
    theme_color: "#11798A",
    icons: [
      { src: "/icon.svg", sizes: "any", type: "image/svg+xml" },
      { src: "/brand/marketing25-mark-192.png", sizes: "192x192", type: "image/png" },
      { src: "/brand/marketing25-mark-512.png", sizes: "512x512", type: "image/png" },
    ],
  };
}
