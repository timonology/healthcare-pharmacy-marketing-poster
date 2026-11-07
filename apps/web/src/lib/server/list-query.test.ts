import { describe, expect, it } from "vitest";
import { listQuery } from "./list-query";

describe("listQuery", () => {
  it("passes sort, from and to through to the API", () => {
    const req = new Request(
      "http://localhost/api/videos?sort=oldest&from=2026-10-01T00%3A00%3A00.000Z&to=2026-11-01T00%3A00%3A00.000Z",
    );
    const q = new URLSearchParams(listQuery(req));
    expect(q.get("sort")).toBe("oldest");
    expect(q.get("from")).toBe("2026-10-01T00:00:00.000Z");
    expect(q.get("to")).toBe("2026-11-01T00:00:00.000Z");
  });

  it("drops anything else", () => {
    expect(listQuery(new Request("http://localhost/api/campaigns?ownerId=someone-else&sort=newest"))).toBe(
      "?sort=newest",
    );
  });

  it("is empty when there is nothing to pass", () => {
    expect(listQuery(new Request("http://localhost/api/videos"))).toBe("");
  });
});
