"""JWT validation. Validates tokens issued by the .NET API."""

from typing import Annotated

from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from jose import JWTError, jwt
from pydantic import BaseModel

from app.config import Settings, get_settings

bearer = HTTPBearer(auto_error=True)


class CurrentUser(BaseModel):
    id: str
    email: str
    name: str


def get_current_user(
    creds: Annotated[HTTPAuthorizationCredentials, Depends(bearer)],
    settings: Annotated[Settings, Depends(get_settings)],
) -> CurrentUser:
    try:
        payload = jwt.decode(
            creds.credentials,
            settings.jwt_secret,
            algorithms=["HS256"],
            audience=settings.jwt_audience,
            issuer=settings.jwt_issuer,
        )
    except JWTError as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid or expired token.",
        ) from exc

    return CurrentUser(
        id=str(payload.get("sub", "")),
        email=str(payload.get("email", "")),
        name=str(payload.get("name", "")),
    )
