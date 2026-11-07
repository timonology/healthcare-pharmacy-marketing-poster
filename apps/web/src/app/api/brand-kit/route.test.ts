import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * GET /api/brand-kit: "no kit yet" is a normal state, not an error.
 *
 * Every new pharmacy opens Brand Kit before saving one. The API says 404; the
 * browser gets 204 so it does not log a red "Failed to load resource" for a
 * page that is working exactly as intended. Real failures still fail.
 */

const apiFetch = vi.fn();
vi.mock("@/lib/server/api-proxy", () => ({ apiFetch: (...a: unknown[]) => apiFetch(...a) }));

const { GET } = await import("./route");

beforeEach(() => apiFetch.mockReset());

describe("GET /api/brand-kit", () => {
  it("answers 204 with no body when the pharmacy has no kit yet", async () => {
    apiFetch.mockResolvedValue({ ok: false, status: 404, data: null, error: "No brand kit configured." });

    const res = await GET();

    expect(res.status).toBe(204);
    expect(await res.text()).toBe("");
  });

  it("returns the kit when there is one", async () => {
    const kit = { id: "k1", name: "Riverside" };
    apiFetch.mockResolvedValue({ ok: true, status: 200, data: kit, error: null });

    const res = await GET();

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual(kit);
  });

  it.each([401, 500, 502])("passes a real failure (%i) through as a failure", async (status) => {
    apiFetch.mockResolvedValue({ ok: false, status, data: null, error: "nope" });

    const res = await GET();

    expect(res.status).toBe(status);
    expect(await res.json()).toEqual({ error: "nope" });
  });
});
