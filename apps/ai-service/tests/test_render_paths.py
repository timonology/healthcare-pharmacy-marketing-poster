"""Renders never write into the working directory.

The service runs as an unprivileged user (security review SEC-14) and its
working directory, /app, belongs to root. MoviePy's ``write_videofile`` puts
its intermediate audio file in the *current directory* unless told otherwise,
so a call without ``temp_audiofile`` fails every render at the Editing stage
with "Permission denied" — on 5 October it did, in the smoke test, and in
nothing else, because tests run as the owner of the checkout.

Checked on the source rather than by rendering: a render needs ffmpeg,
ImageMagick and a voice track, and this rule is about one keyword argument.
"""

import ast
from pathlib import Path

APP = Path(__file__).resolve().parents[1] / "app"


def _calls(name: str):
    for path in APP.rglob("*.py"):
        if "__pycache__" in path.parts:
            continue
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        for node in ast.walk(tree):
            if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == name):
                yield path, node


def test_every_video_write_names_where_its_temporary_audio_goes():
    calls = list(_calls("write_videofile"))
    assert calls, "expected at least one write_videofile call to check"
    for path, call in calls:
        keywords = {k.arg for k in call.keywords}
        assert "temp_audiofile" in keywords, (
            f"{path.name}:{call.lineno} write_videofile without temp_audiofile "
            "writes into the working directory, which the service cannot write to"
        )
