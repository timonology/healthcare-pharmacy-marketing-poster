from functools import lru_cache

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Loaded from environment variables (12-factor)."""

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    @field_validator(
        "anthropic_api_key", "tavily_api_key", "pexels_api_key", "elevenlabs_api_key",
        "azure_openai_api_key", mode="before",
    )
    @classmethod
    def _clean_key(cls, v: object) -> object:
        """Trim what a dashboard paste adds: whitespace, a newline, wrapping quotes.

        Railway and Azure store a value exactly as typed. A key pasted as
        "sk-ant-..." (with the quotes from a .env line) or with a trailing newline
        is sent as-is and refused with 401 invalid x-api-key, while the same key
        works locally where the .env parser strips both. Empty after trimming
        means not set.
        """
        if not isinstance(v, str):
            return v
        v = v.strip()
        if len(v) >= 2 and v[0] == v[-1] and v[0] in "\"'":
            v = v[1:-1].strip()
        return v or None

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

    # ─── Video agent ───────────────────────────────────────────────────
    # Anthropic Claude — script generation.
    anthropic_api_key: str | None = Field(None, alias="ANTHROPIC_API_KEY")
    anthropic_model: str = Field("claude-haiku-4-5-20251001", alias="ANTHROPIC_MODEL")

    # Poster template design uses a stronger model than the video scriptwriter.
    # Designing a template is long, highly structured JSON generation with
    # dozens of interacting geometric constraints; a small model fails
    # validation often enough that the repair rounds cost more than the better
    # model would have. Templates are also authored rarely and reused forever,
    # so per-call cost barely matters and quality matters a lot.
    template_design_model: str = Field("claude-sonnet-5", alias="TEMPLATE_DESIGN_MODEL")

    # Tavily — research node.
    tavily_api_key: str | None = Field(None, alias="TAVILY_API_KEY")

    # Pexels — stock B-roll.
    pexels_api_key: str | None = Field(None, alias="PEXELS_API_KEY")

    # ElevenLabs.
    #
    # Setting this key makes ElevenLabs *the* narrator — there is no fallback to
    # Edge-TTS once it is set. Edge-TTS is an unofficial route into Microsoft's
    # consumer speech service and is not served to datacentre address ranges, so
    # on Railway and Azure it fails every render at the voice stage. See
    # video_agent/tts_policy.py.
    elevenlabs_api_key: str | None = Field(None, alias="ELEVENLABS_API_KEY")

    # Which ElevenLabs voice narrates when the video did not name one.
    #
    # Left unset, the first voice on the account is used and logged. No id is
    # hard-coded here on purpose: a voice id in this file is a guess about
    # somebody else's account, and a wrong guess fails as a 404 that reads like
    # a bug in our code rather than a setting nobody filled in.
    elevenlabs_default_voice_id: str | None = Field(
        None, alias="ELEVENLABS_DEFAULT_VOICE_ID"
    )

    # Replicate (optional; only used when visualStyle=aiImagery).
    replicate_api_token: str | None = Field(None, alias="REPLICATE_API_TOKEN")
    replicate_flux_model: str = Field(
        "black-forest-labs/flux-schnell", alias="REPLICATE_FLUX_MODEL"
    )
    ai_imagery_max_images_per_job: int = Field(8, alias="AI_IMAGERY_MAX_IMAGES_PER_JOB")
    ai_imagery_budget_per_job_usd: float = Field(0.05, alias="AI_IMAGERY_BUDGET_PER_JOB_USD")
    ai_imagery_cost_per_image_usd: float = Field(0.003, alias="AI_IMAGERY_COST_PER_IMAGE_USD")

    # ─── What a render costs us ────────────────────────────────────────
    #
    # All None by default, and that is the point. A render always records what
    # it consumed — characters, tokens, searches — and converts to money only
    # where a rate has been set here. An unset rate leaves the units on record
    # with no amount, which reads as "we have not priced this yet".
    #
    # The alternative, a plausible default, is worse than nothing: it produces a
    # total that looks authoritative, is wrong by whatever the real contract
    # says, and hides the fact that nobody ever checked. The refine charge
    # policy is waiting on these numbers, so a made-up one would be acted on.
    #
    # Per *unit*, not per thousand. Providers quote per-million tokens and
    # per-thousand characters, so these are small decimals — but a rate that
    # needs dividing before it is stored is a rate somebody will eventually
    # store undivided.
    elevenlabs_usd_per_character: float | None = Field(
        None, alias="ELEVENLABS_USD_PER_CHARACTER"
    )
    anthropic_usd_per_input_token: float | None = Field(
        None, alias="ANTHROPIC_USD_PER_INPUT_TOKEN"
    )
    anthropic_usd_per_output_token: float | None = Field(
        None, alias="ANTHROPIC_USD_PER_OUTPUT_TOKEN"
    )
    tavily_usd_per_search: float | None = Field(None, alias="TAVILY_USD_PER_SEARCH")

    # MongoDB — same connection as the .NET API.
    mongo_connection_string: str | None = Field(None, alias="Mongo__ConnectionString")
    mongo_database: str = Field("acme", alias="Mongo__Database")

    # Azure Blob — same account/container as the .NET API.
    azure_blob_connection_string: str | None = Field(None, alias="AzureBlob__ConnectionString")
    azure_blob_container: str = Field("canvas-assets", alias="AzureBlob__Container")

    # GET /health/ready, for `pnpm smoke`. Same value as the API's
    # Readiness__Token. Unset: the endpoint answers 404.
    readiness_token: str | None = Field(None, alias="READINESS_TOKEN")

    # Worker tuning.
    video_worker_enabled: bool = Field(True, alias="VIDEO_WORKER_ENABLED")
    video_worker_poll_seconds: float = Field(4.0, alias="VIDEO_WORKER_POLL_SECONDS")


@lru_cache
def get_settings() -> Settings:
    return Settings()  # type: ignore[call-arg]
