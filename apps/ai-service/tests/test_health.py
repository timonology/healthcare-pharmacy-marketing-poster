from fastapi.testclient import TestClient

from app.main import BUILD_ID, app


def test_health_reports_alive() -> None:
    client = TestClient(app)
    res = client.get("/health")
    assert res.status_code == 200
    assert res.json()["status"] == "ok"


def test_health_reports_the_running_build() -> None:
    """The endpoint must name the build, and the same one the boot marker does.

    This asserted ``res.json() == {"status": "ok"}`` — exact equality — and the
    strictness was right for a liveness contract other things poll. It broke
    when ``build`` was added, which is the correct behaviour for a test like
    that: a response shape changed and something noticed.

    Rewritten rather than loosened, because the reason ``build`` exists is worth
    pinning. A five-day-old container once served a 404 for an endpoint that had
    shipped days earlier, and nothing on this service could say which build was
    answering — the boot marker was a hand-typed constant that always printed
    the same thing. Asserting only ``status == "ok"`` would let that field be
    dropped again without a sound.

    Compared against ``app.main.BUILD_ID`` rather than a literal: the endpoint
    and the boot marker have to report the same value, or "which build is
    running?" gets two answers depending on where you look.
    """
    client = TestClient(app)
    body = client.get("/health").json()

    assert "build" in body, (
        "/health no longer reports the build. It is how a stale container is "
        "diagnosed without log access — see the boot marker in app/main.py."
    )
    assert body["build"], "the build must not be empty"
    assert body["build"] == BUILD_ID


def test_the_refine_endpoint_is_registered() -> None:
    """The route whose absence looked like a comprehension failure.

    A stale image did not serve ``/api/video/classify-refine``, so the .NET API
    received a 404 and reported it to the pharmacist as "we could not work out
    what to change just now" — a deploy problem wearing the costume of a model
    problem. The image was the fault, not the routing, and this test would not
    have caught it.

    It is here for the other half: the router is mounted with ``prefix="/api"``
    while declaring ``prefix="/video"`` of its own, so the full path is assembled
    from two places. Getting that wrong produces exactly the same 404 with
    exactly the same misleading message, and this pins the path the .NET client
    actually calls.

    Asserted by calling it rather than by reading ``app.routes``. Inspecting the
    router would mean asserting how FastAPI composes prefixes, which is the very
    thing in doubt — a test that shares its assumption with the code it checks
    agrees with it whether or not either is right. An unauthenticated request is
    unambiguous: anything other than 404 means the path exists.
    """
    client = TestClient(app)
    res = client.post("/api/video/classify-refine", json={"instruction": "x"})

    assert res.status_code != 404, (
        "/api/video/classify-refine is not routed. The .NET classifier calls "
        "exactly this path, and a 404 reaches the pharmacist as 'we could not "
        "work out what to change just now'."
    )
    assert res.status_code in (401, 403), (
        f"expected an auth refusal without a token, got {res.status_code}"
    )


def test_the_narration_endpoint_is_registered() -> None:
    """The voice list the .NET API relays to the video dialog.

    Same two-prefix assembly as the refine endpoint above — ``prefix="/video"``
    on the router, ``prefix="/api"`` at the mount — and the same consequence if
    it is wrong, one step removed: the API reads a 404, returns 204, and the
    dialog quietly offers no voice choice at all. Nothing errors anywhere, so
    the symptom is a missing control rather than a failure.

    Called rather than read off ``app.routes``, for the reason given above: a
    test that inspects how FastAPI composes prefixes shares its assumption with
    the code under test.
    """
    client = TestClient(app)
    res = client.get("/api/video/narration")

    assert res.status_code != 404, (
        "/api/video/narration is not routed. AiNarrationCatalog calls exactly "
        "this path; a 404 there becomes a video dialog with no voice picker."
    )
    assert res.status_code in (401, 403), (
        f"expected an auth refusal without a token, got {res.status_code}"
    )
