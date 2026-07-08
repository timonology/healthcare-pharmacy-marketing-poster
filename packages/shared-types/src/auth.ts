import type { SubscriptionTier } from "./subscription";

export interface RegisterRequest {
  email: string;
  password: string;
  displayName: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RefreshRequest {
  refreshToken: string;
}

export interface AuthResponse {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
  user: UserProfile;
}

export interface UserProfile {
  id: string;
  email: string;
  displayName: string;
  tier: SubscriptionTier;
  onboardingCompleted: boolean;
  createdAtUtc: string;
}

export interface JwtClaims {
  sub: string;
  email: string;
  name: string;
  iss: string;
  aud: string;
  exp: number;
  iat: number;
}
