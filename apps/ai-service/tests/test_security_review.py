"""ai-service fixes from the 3 October 2026 security reviews (SEC-08, SEC-17, R2-11).

See docs/SECURITY_REVIEW_2026-10-03.pdf.
"""

from __future__ import annotations

import asyncio

import pytest
from fastapi import HTTPException
from fastapi.testclient import TestClient

from app.main import app
from app.routers.canvas import suggest
from app.security import CurrentUser, _Window, per_user_limit, require_staff

PHARMACY = CurrentUser(id="u-1", email="p@x.test", name="P", role="PharmacyUser")
STAFF = CurrentUser(id="s-1", email="s@x.test", name="S", role="Admin")


def test_the_window_allows_up_to_the_limit_then_refuses() -> None:
    w = _Window()
    assert all(w.allow("k", 3, 60, now=100.0 + i) for i in range(3))
    assert not w.allow("k", 3, 60, now=103.0)
    assert w.allow("other", 3, 60, now=103.0), "one user's limit is not another's"


def test_the_window_slides() -> None:
    w = _Window()
    for i in range(3):
        w.allow("k", 3, 60, now=100.0 + i)
    assert not w.allow("k", 3, 60, now=150.0)
    assert w.allow("k", 3, 60, now=161.0), "calls older than the window no longer count"


def test_a_pharmacy_is_limited_and_staff_are_not() -> None:
    dep = per_user_limit("test-limit-unique", 2)
    dep(PHARMACY)
    dep(PHARMACY)
    with pytest.raises(HTTPException) as refused:
        dep(PHARMACY)
    assert refused.value.status_code == 429

    for _ in range(10):
        assert dep(STAFF) is STAFF


def test_classify_refine_is_rate_limited() -> None:
    """Pinned by inspecting the dependency, so removing it fails a test."""
    from app.routers import video

    route = next(r for r in video.router.routes if r.path.endswith("classify-refine"))
    calls = [d.call for d in route.dependant.dependencies]
    assert any(getattr(c, "__qualname__", "").startswith("per_user_limit") for c in calls), (
        "classify-refine lost its per-user limit (SEC-08)")


def test_suggest_is_staff_only() -> None:
    from app.routers import canvas

    route = next(r for r in canvas.router.routes if r.path.endswith("/suggest"))
    calls = [d.call for d in route.dependant.dependencies]
    assert require_staff in calls, "/canvas/suggest must be staff-only (SEC-08)"
    assert suggest is route.endpoint


def test_interactive_docs_are_off_unless_app_env_is_development() -> None:
    client = TestClient(app)
    # The test run does not set APP_ENV=development.
    assert client.get("/docs").status_code == 404
    assert client.get("/redoc").status_code == 404
    # The schema stays: the smoke and e2e checks read it.
    assert client.get("/openapi.json").status_code == 200


def test_jwt_diagnostics_are_development_only() -> None:
    """Follow-up review R2-11: an anonymous secret fingerprint is an oracle."""
    client = TestClient(app)
    assert client.get("/diagnostics/jwt").status_code == 404


def test_a_model_failure_does_not_reach_the_caller_verbatim() -> None:
    from app.chains.template_designer import TemplateGenerationError, generate_template

    class Boom:
        async def ainvoke(self, _messages):
            raise RuntimeError("deployment gpt-secret-name quota 0 at https://internal.example")

    with pytest.raises(TemplateGenerationError) as err:
        asyncio.run(generate_template(
            "A flu clinic poster", owner_id="o", photo={"mode": "none"}, llm=Boom()))
    assert "internal.example" not in str(err.value)
    assert "gpt-secret-name" not in str(err.value)
