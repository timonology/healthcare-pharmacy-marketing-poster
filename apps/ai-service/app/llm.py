"""One place that builds an Anthropic client.

**Why this exists.** Five call sites constructed ``ChatAnthropic`` by hand, and
four of them passed ``temperature``. Newer Claude models reject it outright —
the API answers ``400 invalid_request_error: `temperature` is deprecated for
this model`` — so every one of those four is a render or a classification that
dies at the first model call.

The fifth had already found this out. ``template_designer._llm`` carries a
docstring explaining that temperature is refused, and dismisses the risk to the
others with "the video pipeline's older Haiku model is configured separately,
so there is nothing here that needs it". That was true when it was written. The
video pipeline now runs ``claude-sonnet-5``, the assumption expired silently,
and the lesson sat one directory away from the code that needed it while
``researcher`` failed on its first call.

So the knowledge lives in a function instead of a comment. A model is chosen by
the caller; how to talk to Anthropic is decided here, once.

**No temperature, at all.** Not "unless the model dislikes it" — there is no
list of which models do, the list would drift, and a wrong entry is a 400 on a
customer's render. Output variety comes from the prompt; determinism where it
matters comes from asking for JSON and validating it. Neither was ever really
being bought with this parameter: the classifier passed ``temperature=0`` and
still needed a strict parser, because a temperature of zero is not a guarantee.
"""

from __future__ import annotations

from langchain_anthropic import ChatAnthropic

from app.config import get_settings

#: Long enough for a design, short enough that a wedged call does not hold a
#: render lane open until the lease lapses.
DEFAULT_TIMEOUT_SECONDS = 180


def anthropic_chat(
    model: str,
    *,
    max_tokens: int = 4096,
    timeout: int = DEFAULT_TIMEOUT_SECONDS,
    api_key: str | None = None,
) -> ChatAnthropic:
    """A configured client for ``model``.

    ``api_key`` is read from settings unless given, so callers that already hold
    a settings object do not have to thread one through.
    """
    key = api_key if api_key is not None else get_settings().anthropic_api_key
    return ChatAnthropic(
        model=model,
        api_key=key,
        max_tokens=max_tokens,
        timeout=timeout,
    )


def text_of(reply: object) -> str:
    """The plain text of a model reply, whatever shape it arrived in.

    **Why this is not just ``reply.content``.** Claude returns *content blocks*,
    so ``content`` is a list like ``[{"type": "text", "text": "..."}]`` — not a
    string. Newer models and newer ``langchain-anthropic`` versions return the
    list far more often than the string, and every caller here assumed a string:

    * ``_extract_json(resp.content)`` handed a list straight to ``re.search``,
      which raises ``TypeError: expected string or bytes-like object, got
      'list'`` — the scriptwriter died on it, and the raw message was shown to
      the pharmacist;
    * ``getattr(reply, "content", "")`` returns the list, so the two classifiers
      would parse a repr and quietly fall back to "unclear";
    * ``str(reply.content)`` produces ``[{'type': 'text', 'text': '...'}]``,
      which is Python repr, not JSON. It *sometimes* parses, because a brace
      search can still find the payload inside — the worst kind of wrong, since
      it works until it doesn't.

    This is the third provider-shape bug in this pipeline in two days (Tavily
    returning strings, Anthropic rejecting ``temperature``, and now this). The
    pattern is always the same: a third party is free to change its return
    shape, and code that reads it directly is code that breaks in production
    without a test failing anywhere. So the boundary is normalised in one place,
    and callers are not allowed to see the raw shape.

    Unknown shapes degrade to ``str`` rather than raising. A parser downstream
    can report "we could not read that" to somebody; a ``TypeError`` from inside
    a regex cannot.
    """
    content = getattr(reply, "content", reply)

    if isinstance(content, str):
        return content

    if isinstance(content, list):
        parts: list[str] = []
        for block in content:
            if isinstance(block, str):
                parts.append(block)
            elif isinstance(block, dict):
                # {"type": "text", "text": ...}. Non-text blocks — tool calls,
                # thinking — carry no prose and are skipped rather than
                # stringified into the middle of the JSON we are about to parse.
                if block.get("type", "text") == "text" and isinstance(block.get("text"), str):
                    parts.append(block["text"])
            else:
                text = getattr(block, "text", None)
                if isinstance(text, str):
                    parts.append(text)
        return "".join(parts)

    return "" if content is None else str(content)
