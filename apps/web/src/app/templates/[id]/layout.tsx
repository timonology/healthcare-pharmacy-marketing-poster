import type { Metadata } from "next";
import { pageMetadata } from "@/lib/site";

const API_BASE =
  process.env.API_INTERNAL_URL ?? process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5050";

interface PublicTemplate {
  name: string;
  description?: string;
  category?: string;
}

/**
 * Each template page gets its own title and description, so a search for
 * "flu vaccination poster template" can land on the template itself.
 *
 * The page is drawn in the browser; this asks the API (anonymously, so it is
 * not recorded as a view) only for the words. Cached for an hour. If the API
 * is down or the template unpublished, the page keeps generic metadata and is
 * kept out of the index rather than failing to render.
 */
export async function generateMetadata(
  { params }: { params: Promise<{ id: string }> },
): Promise<Metadata> {
  const { id } = await params;
  const path = `/templates/${encodeURIComponent(id)}`;
  const template = await load(id);

  if (!template?.name) {
    return { title: "Poster template", robots: { index: false, follow: true } };
  }

  const title = `${template.name} poster template`;
  const description = clip(
    (template.description ?? "").trim()
      || `A print-ready ${(template.category ?? "pharmacy").toLowerCase()} poster for UK community pharmacies, with your logo and contact details added for you.`,
    160,
  );

  return { title, ...pageMetadata({ title, description, path }) };
}

async function load(id: string): Promise<PublicTemplate | null> {
  try {
    const res = await fetch(`${API_BASE}/api/templates/${encodeURIComponent(id)}`, {
      next: { revalidate: 3600 },
      signal: AbortSignal.timeout(4000),
    });
    return res.ok ? ((await res.json()) as PublicTemplate) : null;
  } catch {
    return null;
  }
}

function clip(text: string, max: number): string {
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length <= max ? flat : `${flat.slice(0, max - 1).trimEnd()}…`;
}

export default function TemplateLayout({ children }: { children: React.ReactNode }) {
  return children;
}
