"""The provider boundary, tested with the shapes providers actually send.

Three production failures in two days came through this boundary, and not one
of them failed a test first:

* Anthropic began rejecting ``temperature`` — 400 on every render;
* Tavily returned a list of strings where dictionaries were expected —
  ``AttributeError`` in the researcher, the first node in the graph;
* Anthropic returned content *blocks* where a string was expected —
  ``TypeError: expected string or bytes-like object, got 'list'``, shown to a
  pharmacist verbatim.

They were invisible because the suite mocks the providers, which is the right
thing to do — nobody wants a test that costs money and flakes. The consequence
is that *what the provider sends* is untested by construction, so the code that
reads it has to be tested against the shapes instead. That is what this file is.

Every case below is a shape one of these libraries has actually returned.
"""

from app.llm import text_of


class _Reply:
    """Minimal stand-in for a LangChain message."""

    def __init__(self, content: object) -> None:
        self.content = content


class _Block:
    """A content block object rather than a dict — some versions return these."""

    def __init__(self, text: str) -> None:
        self.text = text
        self.type = "text"


def test_a_plain_string_is_returned_unchanged() -> None:
    assert text_of(_Reply("hello")) == "hello"


def test_content_blocks_are_flattened() -> None:
    """The shape that killed the scriptwriter in production."""
    reply = _Reply([{"type": "text", "text": '{"script": "hi"}'}])
    assert text_of(reply) == '{"script": "hi"}'


def test_multiple_blocks_join_in_order() -> None:
    reply = _Reply(
        [{"type": "text", "text": '{"a":'}, {"type": "text", "text": ' 1}'}]
    )
    assert text_of(reply) == '{"a": 1}'


def test_non_text_blocks_are_skipped_not_stringified() -> None:
    """A tool-use block must not land in the middle of JSON about to be parsed.

    Stringifying it would produce something a brace search can still *almost*
    read, which is worse than dropping it: the parse succeeds on the wrong
    thing.
    """
    reply = _Reply(
        [
            {"type": "thinking", "thinking": "hmm"},
            {"type": "text", "text": '{"ok": true}'},
            {"type": "tool_use", "id": "x", "name": "y", "input": {}},
        ]
    )
    assert text_of(reply) == '{"ok": true}'


def test_block_objects_are_read_too() -> None:
    assert text_of(_Reply([_Block("one"), _Block(" two")])) == "one two"


def test_a_list_of_bare_strings_is_joined() -> None:
    assert text_of(_Reply(["a", "b"])) == "ab"


def test_unknown_shapes_degrade_instead_of_raising() -> None:
    """Never raise from here.

    A parser downstream can tell somebody "we could not read that". A TypeError
    thrown from inside a regex cannot — it escapes as a stack frame and, in this
    pipeline, went onto a customer's screen.
    """
    assert text_of(_Reply(None)) == ""
    assert text_of(_Reply(42)) == "42"
    assert text_of("already a string") == "already a string"


def test_the_result_is_always_a_string() -> None:
    """The property every caller depends on.

    ``_extract_json`` hands this straight to ``re.search``, which accepts only
    str or bytes. Anything else is the production failure repeating.
    """
    for content in (
        "x",
        [],
        [{"type": "text", "text": "x"}],
        [{"type": "tool_use"}],
        None,
        123,
        {"unexpected": "mapping"},
    ):
        assert isinstance(text_of(_Reply(content)), str), content
