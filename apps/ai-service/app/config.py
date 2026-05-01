from functools import lru_cache

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Loaded from environment variables (12-factor)."""

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    # JWT — must match the .NET API's signing config exactly.
    jwt_secret: str = Field(..., alias="JWT_SECRET")
    jwt_issuer: str = Field("acme-api", alias="JWT_ISSUER")
    jwt_audience: str = Field("acme-clients", alias="JWT_AUDIENCE")

    # Azure OpenAI.
    azure_openai_endpoint: str | None = Field(None, alias="AZURE_OPENAI_ENDPOINT")
    azure_openai_api_key: str | None = Field(None, alias="AZURE_OPENAI_API_KEY")
    azure_openai_api_version: str = Field(
        "2024-08-01-preview", alias="AZURE_OPENAI_API_VERSION"
    )
    azure_openai_deployment: str = Field("gpt-4o-mini", alias="AZURE_OPENAI_DEPLOYMENT")


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]
