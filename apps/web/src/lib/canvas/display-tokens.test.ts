import { describe, expect, it } from "vitest";
import { displayTokens, tokenValuesFrom } from "./display-tokens";

/**
 * {{tokens}} on screen: the pharmacy's details, or blank — never raw braces —
 * while the stored text keeps the token so it fills in after onboarding.
 */
describe("displayTokens", () => {
  const line = "{{pharmacy_name}} | {{address}} | {{phone}}";

  it("shows blanks for a pharmacy that skipped onboarding", () => {
    const skipped = tokenValuesFrom({ pharmacyName: "", address: "", postCode: "", contactPhone: "" });
    expect(displayTokens(line, skipped)).toBe(" |  | ");
  });

  it("shows blanks while the details are still loading", () => {
    expect(displayTokens(line, null)).not.toContain("{{");
  });

  it("fills in the details once onboarding is done, as print does", () => {
    const values = tokenValuesFrom({
      pharmacyName: "Riverside Pharmacy",
      address: "12 High Street",
      postCode: "M1 4AB",
      contactPhone: "0161 496 0000",
    });
    expect(displayTokens(line, values)).toBe("Riverside Pharmacy | 12 High Street, M1 4AB | 0161 496 0000");
  });

  it("covers every spelling the API accepts", () => {
    const text = "{{ Pharmacy Name }} {{post code}} {{contact_name}} {{booking link}}";
    expect(displayTokens(text, {})).toBe("   ");
  });

  it("leaves text without tokens alone", () => {
    expect(displayTokens("Walk-in flu clinic", null)).toBe("Walk-in flu clinic");
  });
});
