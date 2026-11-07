/**
 * BrandKit contracts. Mirrored as C# DTOs in Acme.Application.BrandKit.
 */

export interface BrandColors {
  primary: string;
  secondary: string;
  accent: string;
}

export interface PharmacyDetails {
  name: string;

  /**
   * Optional. It was required until the booking link moved here — a brand kit
   * could then only exist for a pharmacy that had typed a registration number
   * in, and pharmacies with a booking link and no kit had nowhere for it to go.
   */
  licenseNumber: string;

  phone: string;
  address: string;

  /**
   * Where a patient books. The single stored copy of this URL.
   *
   * It is what the booking QR encodes and what `{{booking_url}}` resolves to,
   * so changing it here updates every poster already made. The profile page
   * shows it and links back here; it cannot be written from there.
   *
   * Omit to leave what is on file; send an empty string to clear it. That
   * distinction is load-bearing — treating "absent" as "clear it" silently wiped
   * booking links once already, when onboarding re-saved a profile without
   * carrying the field.
   */
  bookingUrl?: string | null;
}

export interface BrandKit {
  id: string;
  ownerId: string;
  name: string;
  /** Storage key (server-internal). Use `logoUrl` for rendering. */
  logoBlobKey: string | null;
  /** SAS-signed read URL valid for ~15 minutes. Re-fetch if expired. */
  logoUrl: string | null;
  colors: BrandColors;
  pharmacy: PharmacyDetails;
  regulatoryFooter: string;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface UpsertBrandKitRequest {
  name: string;
  colors: BrandColors;
  pharmacy: PharmacyDetails;
  regulatoryFooter: string;
}
