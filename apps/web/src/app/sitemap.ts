import type { MetadataRoute } from "next";
import { siteUrl } from "@/lib/site";

const API_BASE =
  process.env.API_INTERNAL_URL ?? process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5050";

/** Per request (data cached hourly): the host comes from SITE_URL on the running container, not the build. */
export const dynamic = "force-dynamic";

/** How long the template list is cached: templates are published a few times a month. */
const revalidate = 3600;

/**
 * The public pages, and one entry per published template.
 *
 * Template pages are real content a pharmacist searches for ("flu
 * vaccination poster template"), so they are listed — house set first, as
 * the gallery orders them. If the API cannot be reached the sitemap still
 * lists the fixed pages rather than failing: a missing sitemap is worse for
 * crawling than a short one.
 */
export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const url = siteUrl();
  const now = new Date();

  const pages: MetadataRoute.Sitemap = [
    { url: `${url}/`, lastModified: now, changeFrequency: "weekly", priority: 1 },
    { url: `${url}/templates`, lastModified: now, changeFrequency: "weekly", priority: 0.9 },
    { url: `${url}/pricing`, lastModified: now, changeFrequency: "monthly", priority: 0.8 },
    { url: `${url}/register`, changeFrequency: "yearly", priority: 0.5 },
    { url: `${url}/privacy`, changeFrequency: "yearly", priority: 0.2 },
    { url: `${url}/terms`, changeFrequency: "yearly", priority: 0.2 },
    { url: `${url}/privacy/data-deletion`, changeFrequency: "yearly", priority: 0.1 },
  ];

  return [...pages, ...(await templatePages(url))];
}

interface TemplatePage {
  items: { id: string; updatedAtUtc?: string; createdAtUtc?: string }[];
  total: number;
}

async function templatePages(url: string): Promise<MetadataRoute.Sitemap> {
  const out: MetadataRoute.Sitemap = [];
  try {
    // The list endpoint caps a page at 100; walk it.
    for (let skip = 0; skip < 2000; skip += 100) {
      const res = await fetch(`${API_BASE}/api/templates?skip=${skip}&take=100`, {
        next: { revalidate },
        signal: AbortSignal.timeout(5000),
      });
      if (!res.ok) break;
      const page = (await res.json()) as TemplatePage;
      for (const t of page.items) {
        const changed = t.updatedAtUtc ?? t.createdAtUtc;
        out.push({
          url: `${url}/templates/${encodeURIComponent(t.id)}`,
          lastModified: changed ? new Date(changed) : undefined,
          changeFrequency: "monthly",
          priority: 0.6,
        });
      }
      if (page.items.length < 100 || skip + 100 >= page.total) break;
    }
  } catch {
    // API unreachable: the fixed pages are still worth serving.
  }
  return out;
}
