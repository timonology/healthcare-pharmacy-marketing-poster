import { describe, expect, it } from "vitest";
import { auditEventQuery } from "@acme/shared-types";

/**
 * The console's query string. What matters: empty filters are left out (an
 * empty `actor=` would be a filter on the API side), and the security view and
 * the changes/views switch reach the API by name.
 */
describe("auditEventQuery", () => {
  it("leaves out empty filters", () => {
    const q = new URLSearchParams(
      auditEventQuery({ tab: "Security", actor: "", pharmacyId: undefined, skip: 0, take: 50 }),
    );
    expect(q.get("tab")).toBe("Security");
    expect(q.has("actor")).toBe(false);
    expect(q.has("pharmacyId")).toBe(false);
    expect(q.get("skip")).toBe("0");
  });

  it("keeps the tab's own switch", () => {
    const q = new URLSearchParams(auditEventQuery({ tab: "Pharmacy", stream: "Views", action: "poster." }));
    expect(q.get("stream")).toBe("Views");
    expect(q.get("action")).toBe("poster.");
  });

  it("encodes values rather than splicing them", () => {
    const q = auditEventQuery({ actor: "a&b=c@x.test" });
    expect(new URLSearchParams(q).get("actor")).toBe("a&b=c@x.test");
    expect(q).not.toContain("&b=");
  });
});
