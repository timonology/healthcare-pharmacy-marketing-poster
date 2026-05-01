/**
 * Auth contracts shared between web, .NET API, and ai-service.
 * Mirrored as C# DTOs in Acme.Application.Auth and Pydantic models in app/schemas/auth.py.
 */

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
  accessTokenExpiresAtUtc: string; // ISO-8601
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
  user: UserProfile;
}

export interface UserProfile {
  id: string;
  email: string;
  displayName: string;
  createdAtUtc: string;
}

/** Decoded JWT claims issued by the .NET API. */
export interface JwtClaims {
  sub: string; // user id
  email: string;
  name: string;
  iss: string;
  aud: string;
  exp: number; // unix seconds
  iat: number;
}
