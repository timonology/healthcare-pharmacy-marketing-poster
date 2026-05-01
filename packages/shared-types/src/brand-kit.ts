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
  licenseNumber: string;
  phone: string;
  address: string;
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
