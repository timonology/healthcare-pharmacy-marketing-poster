"""What one render actually consumed, recorded as it happens.

`DECISION-video-brand-and-refine.md` holds the refine charge policy open on
three numbers, and none of them existed: what Replicate and ElevenLabs cost per
video, how long an editor-only re-encode really takes, and how often refines
reach an expensive stage at all. Until those exist, any price is a guess, so the
paid lanes stay shut. This module is how they come to exist.

**Units are recorded always; money only when a rate is configured.**

That is the whole design, and it is deliberate. Characters synthesised, tokens
spent, images generated, searches run, seconds of encoding — these are facts
about what happened, and they stay true. A price is a fact about a contract on
a particular date, and providers change theirs. Multiplying at write time bakes
a stale number into the record and loses the thing it was derived from; keeping
both means a rate correction can be applied retrospectively to every video ever
rendered, by arithmetic, rather than starting the measurement again.

It also means the instrumentation is useful **before** anybody fills the rates
in. "Six thousand characters of premium narration per video" answers a pricing
question on its own — someone reads it off the provider's price list once.

Nothing here may fail a render. A ledger that throws while recording what a
render cost would lose the render, which is a preposterous trade.
"""
from __future__ import annotations

import logging
import time
from dataclasses import dataclass, field
from typing import Any

log = logging.getLogger(__name__)

#: Providers we spend money with. Named rather than free text so a typo shows up
#: as a missing provider in the totals rather than a silent second category.
ANTHROPIC = "anthropic"
ELEVENLABS = "elevenlabs"
REPLICATE = "replicate"
TAVILY = "tavily"
PEXELS = "pexels"

#: What is being counted, per provider. Also fixed, for the same reason.
CHARACTERS = "characters"
INPUT_TOKENS = "input_tokens"
OUTPUT_TOKENS = "output_tokens"
IMAGES = "images"
SEARCHES = "searches"
CLIPS = "clips"


@dataclass
class UsageLine:
    """One measured quantity, and the money only if we know the rate."""

    provider: str
    unit: str
    quantity: float
    #: None when no rate is configured — *not* zero. Zero is a claim that it was
    #: free, which for a premium voice is the opposite of the truth.
    amount_usd: float | None = None

    def to_dict(self) -> dict[str, Any]:
        row: dict[str, Any] = {
            "provider": self.provider,
            "unit": self.unit,
            "quantity": round(self.quantity, 4),
        }
        if self.amount_usd is not None:
            row["amountUsd"] = round(self.amount_usd, 6)
        return row


@dataclass
class UsageLedger:
    """Accumulates what a render consumed. Cheap, in memory, never raises."""

    lines: list[UsageLine] = field(default_factory=list)
    #: Stage name → seconds. Answers "how long does an editor-only re-encode
    #: take", which decides whether a refine feels like editing or like waiting.
    stage_seconds: dict[str, float] = field(default_factory=dict)

    def record(
        self,
        provider: str,
        unit: str,
        quantity: float,
        rate_per_unit: float | None = None,
    ) -> None:
        """Add a measurement. Swallows its own failures on purpose."""
        try:
            if quantity is None or quantity <= 0:
                return
            amount = None
            if rate_per_unit is not None and rate_per_unit > 0:
                amount = float(quantity) * float(rate_per_unit)
            self.lines.append(UsageLine(provider, unit, float(quantity), amount))
        except Exception as exc:  # noqa: BLE001
            log.warning("Could not record %s/%s usage: %s", provider, unit, exc)

    def stage(self, name: str) -> "_StageTimer":
        """Times a pipeline stage: ``with ledger.stage("editor"): ...``"""
        return _StageTimer(self, name)

    def totals(self) -> dict[str, Any]:
        """The document shape. Safe to call at any point, including on failure.

        ``knownUsd`` is the money we can actually account for, and
        ``unpricedUnits`` says how much of the render it does **not** cover. A
        total with nothing beside it invites being read as the cost of a video,
        when it may be the cost of a fraction of one — and the gap is exactly
        what somebody needs to see to know which rate to go and configure.
        """
        try:
            priced = [l for l in self.lines if l.amount_usd is not None]
            unpriced = [l for l in self.lines if l.amount_usd is None]
            return {
                "lines": [l.to_dict() for l in self.lines],
                "knownUsd": round(sum(l.amount_usd or 0.0 for l in priced), 6),
                "unpricedUnits": sorted({f"{l.provider}:{l.unit}" for l in unpriced}),
                "stageSeconds": {k: round(v, 3) for k, v in self.stage_seconds.items()},
            }
        except Exception as exc:  # noqa: BLE001
            log.warning("Could not total usage: %s", exc)
            return {}


    def increments(self) -> dict[str, float]:
        """Mongo ``$inc`` amounts for the running totals of this video.

        Keyed `usageTotals.<provider>:<unit>`, plus `usageTotals.knownUsd`.

        **Why totals are incremented and not recomputed.** `usage` holds the
        most recent attempt, and `usageAttempts` keeps the last twenty — so a
        video retried enough times loses its earliest attempts from that array.
        These counters never do: `$inc` is atomic, additive, and immune to the
        truncation above it. "What has this video cost us, all attempts
        included" is a question the array cannot answer for ever and this can.

        **Units are incremented too, not just money**, and that is the point
        rather than a flourish. The whole design records units so that spend can
        be computed retrospectively once rates are configured. If only the
        latest attempt's units survived, that retrospective sum would be wrong
        for every video that was ever retried — which is precisely the set of
        videos that cost the most.

        A colon separates provider from unit because Mongo treats a dot in a
        field name as a path.
        """
        try:
            totals: dict[str, float] = {}
            for line in self.lines:
                key = f"usageTotals.{line.provider}:{line.unit}"
                totals[key] = totals.get(key, 0.0) + float(line.quantity)
                if line.amount_usd is not None:
                    money = "usageTotals.knownUsd"
                    totals[money] = totals.get(money, 0.0) + float(line.amount_usd)
            return {k: round(v, 6) for k, v in totals.items()}
        except Exception as exc:  # noqa: BLE001
            log.warning("Could not total usage increments: %s", exc)
            return {}



class _StageTimer:
    def __init__(self, ledger: UsageLedger, name: str) -> None:
        self._ledger, self._name, self._started = ledger, name, 0.0

    def __enter__(self) -> "_StageTimer":
        self._started = time.monotonic()
        return self

    def __exit__(self, *exc: Any) -> bool:
        try:
            elapsed = time.monotonic() - self._started
            # Added, not replaced. The researcher runs more than once when the
            # quality gate rejects thin research, and "how long did research
            # take" means all of it.
            self._ledger.stage_seconds[self._name] = (
                self._ledger.stage_seconds.get(self._name, 0.0) + elapsed
            )
        except Exception:  # noqa: BLE001
            pass
        # Never suppress. A stage that raised must still fail the render.
        return False


def count_characters(text: str | None) -> int:
    """What a character-billed provider will actually charge for.

    The text as sent, not the script as written. They differ: the narration is
    stripped of stage directions and bracketed notes before synthesis, and
    billing follows what went over the wire.
    """
    return len(text or "")


def tokens_from(reply: Any) -> tuple[int, int]:
    """(input, output) tokens off a LangChain reply, or (0, 0).

    Defensive because the shape of `usage_metadata` is the provider's to change,
    and a render must not fail over bookkeeping. Zero means "not reported",
    which `record` then skips rather than writing a zero-cost line that would
    read as "this call was free".
    """
    try:
        usage = getattr(reply, "usage_metadata", None)
        if isinstance(usage, dict):
            return int(usage.get("input_tokens") or 0), int(usage.get("output_tokens") or 0)

        # Older LangChain put it under response_metadata.usage.
        meta = getattr(reply, "response_metadata", None)
        if isinstance(meta, dict):
            usage = meta.get("usage")
            if isinstance(usage, dict):
                return (
                    int(usage.get("input_tokens") or usage.get("prompt_tokens") or 0),
                    int(usage.get("output_tokens") or usage.get("completion_tokens") or 0),
                )
    except Exception:  # noqa: BLE001
        pass
    return 0, 0
