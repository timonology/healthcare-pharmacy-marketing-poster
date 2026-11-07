/**
 * Where browsers send Content-Security-Policy violation reports.
 *
 * While the policy is report-only (lib/csp.ts), this is how we learn what an
 * enforced policy would break before it breaks it: each report is logged as
 * one line — the directive, the blocked source and the page — to be read in
 * the App Service log stream.
 *
 * Anonymous by nature (the browser sends it), so it trusts nothing: the body
 * is capped, only a few known fields are read, each is trimmed, and query
 * strings are dropped from URLs so nothing a page carried in its address (a
 * token, an email) is written to the log. Always 204, never an error a
 * visitor could notice.
 */
const MAX_BODY = 16 * 1024;

export const dynamic = "force-dynamic";

export async function POST(req: Request): Promise<Response> {
  try {
    const text = (await req.text()).slice(0, MAX_BODY);
    const body = JSON.parse(text) as unknown;
    for (const report of reportsIn(body).slice(0, 10)) {
      console.warn(
        `CSP ${report.disposition ?? "report"}: ${report.directive ?? "?"} blocked ` +
          `${report.blocked ?? "?"} on ${report.page ?? "?"}`,
      );
    }
  } catch {
    // Malformed or empty: nothing to record.
  }
  return new Response(null, { status: 204 });
}

interface Report {
  directive?: string;
  blocked?: string;
  page?: string;
  disposition?: string;
}

/** Both report formats: the legacy `csp-report` object and the Reporting API array. */
function reportsIn(body: unknown): Report[] {
  const raw: Record<string, unknown>[] = Array.isArray(body)
    ? body.map((r) => (r as { body?: Record<string, unknown> })?.body ?? {})
    : [((body as { "csp-report"?: Record<string, unknown> })?.["csp-report"]) ?? {}];

  return raw.map((r) => ({
    directive: clean(r["effective-directive"] ?? r["violated-directive"] ?? r.effectiveDirective),
    blocked: cleanUrl(r["blocked-uri"] ?? r.blockedURL),
    page: cleanUrl(r["document-uri"] ?? r.documentURL),
    disposition: clean(r.disposition),
  }));
}

function clean(v: unknown): string | undefined {
  return typeof v === "string" ? v.replace(/[^\x20-\x7e]/g, "").slice(0, 120) : undefined;
}

function cleanUrl(v: unknown): string | undefined {
  const s = clean(v);
  if (!s) return s;
  try {
    const u = new URL(s);
    return `${u.origin}${u.pathname}`.slice(0, 120);
  } catch {
    return s; // "inline", "eval", "data" and the like
  }
}
