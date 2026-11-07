#!/usr/bin/env python3
"""Prove every regression test still catches the bug it was written for.

For each bug below, this puts the bug back into the code, runs the tests that
guard it, and requires at least one of them to FAIL — then restores the file.
A regression test that passes with its bug reinstated protects nothing, and
that is exactly the kind of test that looks fine in review. Run before a
release, or after refactoring any of the files listed:

    pnpm test:prove                 # everything
    pnpm test:prove -- framer       # only bugs whose name contains "framer"

Every file is restored even if a run is interrupted. An "anchor" error means
the code the mutation targets has changed: update the entry so it recreates
the bug in the new code, rather than deleting it.

Each bug's tests are first run on the unmodified code and must PASS. Without
that, a wrong test path or a broken runner fails for its own reasons and is
counted as "caught" — which is exactly how this script once reported a clean
result through a runner command that could not start (a path with spaces,
unquoted). Those appear as BASELINE and fail the run.

Runners default to the real ones — apps/ai-service/.venv/bin/pytest (or
`uv run --extra dev pytest`) and apps/web vitest. Override with PROVE_PY /
PROVE_WEB, each a command that takes one test file and exits non-zero on a
failure. They are split like a shell line, so quote any path with spaces.
"""
import os
import shlex
import shutil
import signal
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
AI, WEB = ROOT / "apps/ai-service", ROOT / "apps/web"


def _py_runner() -> list[str]:
    if os.environ.get("PROVE_PY"):
        return shlex.split(os.environ["PROVE_PY"])
    if (AI / ".venv/bin/pytest").exists():
        return [str(AI / ".venv/bin/pytest"), "-q", "-x"]
    if shutil.which("uv"):
        return ["uv", "run", "--extra", "dev", "pytest", "-q", "-x"]
    return ["pytest", "-q", "-x"]


def _web_runner() -> list[str]:
    if os.environ.get("PROVE_WEB"):
        return shlex.split(os.environ["PROVE_WEB"])
    return [str(WEB / "node_modules/.bin/vitest"), "run"]


def py(*tests):
    return [(AI, [*_py_runner(), t]) for t in tests]


def web(*tests):
    return [(WEB, [*_web_runner(), t]) for t in tests]


API = ROOT / "apps/api"


def dotnet(filter_: str, project: str = "tests/Acme.Tests"):
    """A .NET test run narrowed to one class (``--filter``), so a probe takes seconds."""
    runner = shlex.split(os.environ.get("PROVE_DOTNET", "dotnet test --nologo -v q"))
    return [(API, [*runner, project, "--filter", filter_])]


A = "app/video_agent/"
M = [
 # (bug, file, find, replace, test runs)
 ("narration longer than the plan appended shots that belong to no moment", AI/(A+"beats.py"),
   "    scale = total / sum(planned)", "    scale = 1.0",
   py("tests/test_beats.py")),
 ("a typed request guesses a shot from its description", AI/(A+"shot_edits.py"),
   "    return found or None", "    return found or ([count - 1] if count else None)",
   py("tests/test_shot_edits.py")),
 ("'last' points at the wrong shot", AI/(A+"shot_edits.py"),
   "        elif m.group(\"last\"):\n            index = count - 1", "        elif m.group(\"last\"):\n            index = count - 2",
   py("tests/test_shot_edits.py")),
 ("a tapped shot is overruled", AI/(A+"shot_edits.py"),
   "        {\"index\": item.get(\"index\"), \"query\": _search_for(item.get(\"show\"), topic)}",
   "        {\"index\": 0, \"query\": _search_for(item.get(\"show\"), topic)}",
   py("tests/test_shot_edits.py")),
 ("a chosen shot searches the subject alone", AI/(A+"beats.py"),
   "    candidates = [asked, f\"{asked} {topic}\".strip() if asked and topic else \"\"]",
   "    candidates = [asked, f\"{asked} {topic}\".strip() if asked and topic else \"\", topic]",
   py("tests/test_beats.py")),
 ("stock miss on a chosen shot takes a spare, not this video's clip", AI/(A+"nodes.py"),
   "    if override_missed and stock:", "    if False:",
   py("tests/test_refine_scenarios.py")),
 ("a refine falls back to the generic pharmacy list", AI/(A+"nodes.py"),
   "    if not pool and state.get(\"clip_sources\"):", "    if False:",
   py("tests/test_refine_scenarios.py")),
 ("shot stills taken from the wrong moment", AI/(A+"worker.py"),
   "            middle = (float(shot[\"start\"]) + float(shot[\"end\"])) / 2", "            middle = 0.1",
   py("tests/test_shot_thumbnails.py")),
 ("picker sends the wrong shot or drops the words", WEB/"src/lib/video-refine.ts",
   "      return text ? { index, show: text } : { index };", "      return { index: index + 1 };",
   web("src/lib/video-refine.test.ts")),
 ("picker tile does not select its own shot", WEB/"src/components/video/ShotPicker.tsx",
   "              onClick={() => toggle(shot.index)}", "              onClick={() => toggle(0)}",
   web("src/components/video/ShotPicker.test.tsx")),
 ("last tile not labelled 'last' after a skipped moment", WEB/"src/lib/video-refine.ts",
   "at === total - 1 ? \"last\"", "shot.index === total - 1 ? \"last\"",
   web("src/lib/video-refine.test.ts")),
 ("a re-cut video's tiles show the old stills from cache", WEB/"src/components/video/ShotPicker.tsx",
   "                    version ? `?v=${encodeURIComponent(version)}` : \"\"}`}", "                    \"\"}`}",
   web("src/components/video/ShotPicker.test.tsx")),
 ("shot timeline dropped by the graph: tiles are numbers, no stills", AI/(A+"state.py"),
   "    shot_timeline: list[dict]", "",
   py("tests/test_state_schema.py")),
 ("refine's kept shots dropped by the graph: every shot re-searched", AI/(A+"state.py"),
   "    previous_aligned: list[str]\n", "",
   py("tests/test_state_schema.py")),
 ("a damaged attachment costs a minute per render", AI/(A+"ai_imagery.py"),
   "    if size is None and not _ffmpeg_can_decode(image_path):", "    if False:",
   py("tests/test_damaged_picture.py")),
 ("VIDEO_BITRATE ignored: a re-cut on detailed footage is 16 Mbps", AI/(A+"nodes.py"),
   "        \"-maxrate\", f\"{cap}k\",\n", "",
   py("tests/test_encode_cap.py")),
 # ── house-style templates ──
 ("the editor draws a closed line as an outline while the PDF fills it", WEB/"src/lib/canvas/line-fill.ts",
   "  return shape.closed ? shape.stroke : undefined;", "  return undefined;",
   web("src/lib/canvas/line-fill.test.ts")),
 ("template text whose line wraps differently in the PDF is not caught", AI/"app/tools/house_templates.py",
   "                    if len(line) > pdf_max_chars(width, size):", "                    if False:",
   py("tests/test_house_templates.py")),
 ("a long pharmacy name running into the phone number is not caught", AI/"app/tools/house_templates.py",
   "            if not side_by_side and ay < by < bottom - 2:", "            if False:",
   py("tests/test_house_templates.py")),
 ("a sideways or wide-gamut stock original is cropped as stored", AI/"app/tools/house_template_assets.py",
   "    img = ImageOps.exif_transpose(img)\n", "",
   py("tests/test_house_template_assets.py")),
 ("a wave dipping below the photo it sits on (bare page) is not caught", AI/"app/tools/house_templates.py",
   "            if iy0 < highest < iy1 and lowest > iy1 + 0.5 and not backed:", "            if False:",
   py("tests/test_house_templates.py")),
 ("template copy that the API's contact-details test rejects is not caught here", AI/"app/tools/house_templates.py",
   "    (re.compile(r\"\\bYour Pharmacy\\b\", re.I), \"the 'Your Pharmacy' placeholder (API test)\"),\n", "",
   py("tests/test_house_templates.py")),
 ("a template silently ships without its photo", AI/"app/tools/house_templates.py",
   "PHOTO_FREE = {\"sys-house-antibiotics\", \"sys-house-medicine-returns\"}",
   "PHOTO_FREE = {\"sys-house-antibiotics\", \"sys-house-medicine-returns\", \"sys-house-hay-fever\"}",
   py("tests/test_house_templates.py")),
 ("an arch photo ships with square top corners", AI/"app/tools/house_template_assets.py",
   "        rgba.putalpha(arch_mask(spec.out))\n", "",
   py("tests/test_house_template_assets.py")),
 ("low-contrast template text is not caught", AI/"app/tools/house_templates.py",
   "                if ratio < need:", "                if False:",
   py("tests/test_house_templates.py")),
 ("a baked photo fade is not applied", AI/"app/tools/house_template_assets.py",
   "    mask.paste(ramp, (x, y))\n", "",
   py("tests/test_house_template_assets.py")),
 ("a circle crop ships with square corners", AI/"app/tools/house_template_assets.py",
   "        rgba.putalpha(circle_mask(spec.out[0]))\n", "",
   py("tests/test_house_template_assets.py")),
 # ── videos on screens ──
 ("the kiosk downloads whole videos to cache them", WEB/"src/lib/kiosk-playlist.ts",
   "  return slides.filter((s) => s.kind !== \"Video\" && !!s.imageUrl).map((s) => s.imageUrl);",
   "  return slides.map((s) => s.mediaUrl ?? s.imageUrl);",
   web("src/lib/kiosk-playlist.test.ts")),
 ("a video that failed to play keeps its slot on the wall", WEB/"src/lib/kiosk-playlist.ts",
   "    if (s.kind === \"Video\") return isVideo(s) && !failedVideos.has(slideKey(s));",
   "    if (s.kind === \"Video\") return isVideo(s);",
   web("src/lib/kiosk-playlist.test.ts")),
 ("a re-signed video link counts as a new slide", WEB/"src/lib/kiosk-playlist.ts",
   "slide.mediaUrl!.split(\"?\")[0]", "slide.mediaUrl!",
   web("src/lib/kiosk-playlist.test.ts")),
 ("the safety net cuts a playing video off", WEB/"src/lib/kiosk-playlist.ts",
   "Math.max(seconds * 3, 60) * 1000", "seconds * 1000",
   web("src/lib/kiosk-playlist.test.ts")),
 ("the kiosk's video route drops Range (Safari will not play)", WEB/"src/app/api/display/video/[videoId]/route.ts",
   "const FORWARD_REQUEST = [\"range\", ", "const FORWARD_REQUEST = [",
   web("src/app/api/display/video/[videoId]/route.test.ts")),
 ("the editor offers videos that are not finished", WEB/"src/lib/playlist-items.ts",
   "v.status === \"Ready\" && !!v.downloadUrl", "v.status !== \"Failed\"",
   web("src/lib/playlist-items.test.ts")),
 ("the kiosk's video route drops the cache validators (every loop re-downloads)", WEB/"src/app/api/display/video/[videoId]/route.ts",
   "  \"etag\",\n", "",
   web("src/app/api/display/video/[videoId]/route.test.ts")),
 ("the kiosk's video route lets '..' through to the API path", WEB/"src/app/api/display/video/[videoId]/route.ts",
   "  if (!VIDEO_ID.test(params.videoId)) return new Response(null, { status: 404 });\n", "",
   web("src/app/api/display/video/[videoId]/route.test.ts")),
 ("screen skips every video: the re-run effect plays an element with no source", WEB/"src/components/display/KioskVideo.tsx",
   "    el.src = src;\n", "",
   web("src/components/display/KioskVideo.test.tsx")),
 ("screen skips every video: a play() our teardown cancelled counts as failure", WEB/"src/components/display/KioskVideo.tsx",
   "      if (active && name !== \"AbortError\") {", "      if (true) {",
   web("src/components/display/KioskVideo.test.tsx")),
 ("the editor lets an eleventh video in", WEB/"src/lib/playlist-items.ts",
   "  if (kind === \"Video\" && videoCount(items) >= PLAYLIST_MAX_VIDEOS) return false;\n", "",
   web("src/lib/playlist-items.test.ts")),
 ("the writer's word budget is requested but not enforced", AI/(A+"nodes.py"),
   "        parsed = _within_budget(ctx, parsed, high)\n", "",
   py("tests/test_script_budget.py")),
 ("a script with too few moments ships a video with too few shots", AI/(A+"beats.py"),
   "    while 0 < len(out) < wanted:", "    while False:",
   py("tests/test_beats.py")),
 ("the tightening pass is allowed to merge shots away", AI/(A+"nodes.py"),
   "- Keep EXACTLY the same number of segments.", "- Cut from the middle: merge or drop the least important sentences.",
   py("tests/test_script_budget.py")),
 ("a tightened script whose captions no longer match its words is accepted", AI/(A+"nodes.py"),
   "    if abs(captioned - after) > max(3, after * 0.15):", "    if False:",
   py("tests/test_script_budget.py")),
 ("classifier import hidden in docstring", AI/"app/chains/refine_router.py",
   "        from app.llm import anthropic_chat, text_of\n", "        pass\n",
   py("tests/test_refine_router.py", "tests/test_no_undefined_names.py")),
 ("footage paired by position (legacy)", AI/(A+"beats.py"),
   "free = (i for i in range(segment_count) if i not in pinned)", "free = iter(range(segment_count))",
   py("tests/test_rehydrate_beats.py")),
 ("borrowed moment not recorded", AI/(A+"beats.py"),
   "            borrowed = mine is not None\n", "            mine = None\n            borrowed = False\n",
   py("tests/test_beats.py")),
 ("failed gap filled from clips on screen (old pool[i % n] rule)", AI/(A+"beats.py"),
   "    out = list(beat_paths)\n    uses: dict[str, int] = {}",
   "    pool = [p for p in list(stock) + list(spares) if p]\n    return [p or (pool[i % len(pool)] if pool else '') for i, p in enumerate(beat_paths)]\n    out = list(beat_paths)\n    uses: dict[str, int] = {}",
   py("tests/test_beats.py")),
 ("reused clip cut from the same frames", AI/(A+"beats.py"),
   "    if use <= 0 or spare < target * 0.5:", "    if True:",
   py("tests/test_beats.py")),
 ("top-up in fixed rotation", AI/(A+"beats.py"),
   "        key=lambda p: (uses.get(p, 0), (pool.index(p) - turn) % len(pool)),", "        key=lambda p: (pool.index(p) - turn) % len(pool),",
   py("tests/test_beats.py")),
 ("refine re-downloads clips it already has", AI/(A+"nodes.py"),
   "    already_downloaded: set[str] = {\n        str(r.get(\"pexels_id\")) for r in (state.get(\"clip_sources\") or [])\n        if isinstance(r, dict) and r.get(\"pexels_id\")\n    }",
   "    already_downloaded: set[str] = set()", py("tests/test_refine_scenarios.py")),
 ("photo placement re-guessed from the list", AI/(A+"rehydrate.py"),
   "    if recorded is None:\n        return plan", "    return plan",
   py("tests/test_rehydrate_beats.py", "tests/test_refine_scenarios.py")),
 ("render ignores the refine's photo placement", AI/(A+"nodes.py"),
   "    if ctx.user_pins is not None:", "    if False:", py("tests/test_refine_scenarios.py")),
 ("failed photo recorded as footage", AI/(A+"nodes.py"),
   "        _photo_pins(ctx, segments),\n        own_records,",
   "        {m: i for m, i in _photo_pins(ctx, segments).items() if m in user_clips},\n        own_records,",
   py("tests/test_refine_scenarios.py")),
 ("adding a photo brings back a replaced one", AI/(A+"rehydrate.py"),
   "    keep = sorted(set(shown.values()) | set(range(max(0, len(images) - added), len(images))))",
   "    keep = list(range(len(images)))", py("tests/test_rehydrate_beats.py")),
 ("add-pictures without a count places nothing", AI/(A+"rehydrate.py"),
   "        # Queued by an API that does not record the count yet (an older build,\n", "        return shown\n",
   py("tests/test_rehydrate_beats.py")),
 ("clip that won't download loses its description", AI/(A+"rehydrate.py"),
   "        sources.append({**record, \"path\": path} if record else None)", "        sources.append({**record, \"path\": path} if record and path else None)",
   py("tests/test_rehydrate_beats.py")),
 ("caption re-cut drops the photo", AI/(A+"rehydrate.py"),
   "            for i, path in user_clips(doc, segments, out_dir).items():\n                if i < len(aligned):\n                    aligned[i] = path\n",
   "", py("tests/test_rehydrate_beats.py")),
 ("crashed refine marks the video Failed", AI/(A+"worker.py"),
   "    refine_of_finished = bool(\n        (job.get(\"refine\") or {}).get(\"confirmed\") and job.get(\"blobKey\"))",
   "    refine_of_finished = False", py("tests/test_worker_refine_rules.py")),
 ("refused add-pictures leaves photos on the list", AI/(A+"worker.py"),
   "        **(_undo_added_pictures(job) if finished else {}),\n    }", "    }",
   py("tests/test_worker_refine_rules.py")),
 ("refine failure says 'allowance'", AI/(A+"worker.py"),
   "    return f\"{cause} while making it. Your video is as it was — ask for the change again.\"",
   "    return _friendly_failure(exc)", py("tests/test_worker_refine_rules.py")),
 ("'We cannot removing …'", AI/(A+"worker.py"),
   "    return \" Not possible here: \" + \"; \".join(parts) + \".\"", "    return \" We cannot \" + \", \".join(parts) + \" from here.\"",
   py("tests/test_worker_refine_rules.py")),
 ("duplicate-caption request not understood", AI/(A+"editor_options.py"),
   "    if payload.get(\"remove_repeated_captions\") is True:", "    if False:",
   py("tests/test_refine_outcomes.py")),
 ("same caption flashed twice", AI/(A+"nodes.py"),
   "        if not key or key in seen:", "        if not key:", py("tests/test_captions.py")),
 ("EXIF rotation ignored", AI/(A+"ai_imagery.py"),
   "            if orientation in (None, 1) or not 2 <= int(orientation) <= 8:", "            if True:",
   py("tests/test_upright.py")),
 ("CMYK upright copy fails (sent to PNG)", AI/(A+"ai_imagery.py"),
   "            if upright.mode in (\"RGBA\", \"LA\", \"P\", \"PA\"):", "            if upright.mode not in (\"RGB\", \"L\"):",
   py("tests/test_upright.py")),
 ("unreadable zoom drops the picture", AI/(A+"framing.py"),
   "    if math.isnan(number) or math.isinf(number):\n        return ZOOM_DEFAULT", "    if False:\n        return ZOOM_DEFAULT",
   py("tests/test_framing.py")),
 ("banker's rounding in geometry", AI/(A+"framing.py"),
   "    return max(2, 2 * math.floor(value / 2 + 0.5))", "    return max(2, 2 * round(value / 2))",
   py("tests/test_framing.py")),
 ("zoom not passed to the clip maker", AI/(A+"user_media.py"),
   "                focal=focal, zoom=zoom)", "                focal=focal)", py("tests/test_user_media.py")),
 # ── web ──
 ("framer: Maximum update depth loop (inline ref + fresh state)", WEB/"src/components/video/PhotoFramer.tsx",
   [("      prev && prev.src === from && prev.w === w && prev.h === h ? prev : { src: from, w, h },", "      ({ src: from, w, h }),"),
    ("            ref={measureIfLoaded}\n            onLoad={(e) => measure(e.currentTarget)}\n            draggable={false}\n            className=\"h-full w-full object-cover\"",
     "            ref={(el) => { if (el?.complete) measure(el); }}\n            onLoad={(e) => measure(e.currentTarget)}\n            draggable={false}\n            className=\"h-full w-full object-cover\"")],
   None, web("src/components/video/PhotoFramer.test.tsx")),
 ("framer: trackpad pinch stuck at 100%", WEB/"src/components/video/PhotoFramer.tsx",
   "        wheelZoom.current !== null && clampZoom(wheelZoom.current) === current",
   "        false && clampZoom(wheelZoom.current ?? 1) === current", web("src/components/video/PhotoFramer.test.tsx")),
 ("framer: plain scroll swallowed", WEB/"src/components/video/PhotoFramer.tsx",
   "      if (!e.ctrlKey && !e.metaKey) return;", "", web("src/components/video/PhotoFramer.test.tsx")),
 ("framer: drag on an axis with no slack moves it", WEB/"src/components/video/PhotoFramer.tsx",
   "      const ny = state.pansDown ?", "      const ny = true ?", web("src/components/video/PhotoFramer.test.tsx")),
 ("framer: zoomed preview not where the render puts it", WEB/"src/components/video/PhotoFramer.tsx",
   "          left: `${(g.x / VIDEO_FRAME.width) * 100}%`,", "          left: \"0%\",", web("src/components/video/PhotoFramer.test.tsx")),
 ("framer: slider squeezed beside its label", WEB/"src/components/video/PhotoFramer.tsx",
   "    <label className=\"block text-[11px] leading-tight text-muted-foreground\">",
   "    <label className=\"flex items-center gap-2 text-[11px] text-muted-foreground\">", web("src/components/video/PhotoFramer.test.tsx")),
 ("create dialog drops the framing", WEB/"src/lib/video-refine.ts",
   "    focalX,\n    focalY,\n    zoom,\n  }));", "  }));", web("src/lib/video-refine.test.ts")),
 ("refused change silent", WEB/"src/lib/video-refine.ts",
   "        ? { kind: \"not-made\"", "        ? false && { kind: \"not-made\"", web("src/lib/video-refine.test.ts")),
 ("dialog cannot scroll", WEB/"src/components/ui/dialog.tsx",
   " overflow-y-auto overscroll-contain", "", web("src/components/ui/dialog.test.ts")),
 ("dialog refusal rendered above the fields, off screen", WEB/"src/app/videos/page.tsx",
   [("          {error && (\n            <p className=\"text-sm text-destructive\" role=\"alert\">{error}</p>\n          )}\n\n          <DialogFooter>",
     "          <DialogFooter>"),
    ("            <Input\n              id=\"name\"",
     "            {error && (<p role=\"alert\">{error}</p>)}\n            <Input\n              id=\"name\"")],
   None, web("src/components/ui/dialog-errors.test.ts")),
 # ── audit log ──
 ("a mutating endpoint ships with no audit mark", ROOT/"apps/api/src/Acme.WebApi/Controllers/PostersController.cs",
   "    [Audit(\"poster.delete\", \"poster\")]\n", "",
   [(ROOT, ["node", "scripts/check-audit.mjs"])]),
 ("an endpoint opts out of auditing without saying why", ROOT/"apps/api/src/Acme.WebApi/Controllers/AuthController.cs",
   "[NoAudit(\"Token refresh runs every few minutes per session; sign-in and sign-out are the security events.\")]",
   "[NoAudit(\"\")]",
   [(ROOT, ["node", "scripts/check-audit.mjs"])]),
 ("a view logged on a mutating verb", ROOT/"apps/api/src/Acme.WebApi/Controllers/PostersController.cs",
   "    [Audit(\"poster.update\", \"poster\")]", "    [ActivityLog(\"poster.update\", \"poster\")]",
   [(ROOT, ["node", "scripts/check-audit.mjs"])]),
 ("console sends an empty filter the API reads as 'match nothing'", ROOT/"packages/shared-types/src/audit.ts",
   "    if (v === undefined || v === null || v === \"\") continue;", "    if (v === undefined) continue;",
   web("src/lib/audit-query.test.ts")),
 # ── house-style AI design ──
 ("a circle portrait is baked as a square photo", AI/"app/chains/house_style.py",
   "        rgba.putalpha(circle_mask(slot.width) if slot.circle else arch_mask((slot.width, slot.height)))\n", "",
   py("tests/test_house_style.py")),
 ("a photo is stretched to its slot instead of cover-cropped", AI/"app/chains/house_style.py",
   "    return img.crop((x0, y0, x0 + cw, y0 + ch)).resize(", "    return img.resize(",
   py("tests/test_house_style.py")),
 ("a fade faked with a translucent rect passes the house check", AI/"app/chains/house_style.py",
   "        if s.get(\"kind\") == \"rect\" and float(s.get(\"opacity\", 1)) < 1:",
   "        if False:",
   py("tests/test_house_style.py")),
 ("an invented pharmacy name gets onto a generated poster", AI/"app/chains/house_style.py",
   "            if (named := _invented_pharmacy(value)) is not None:", "            if (named := None) is not None:",
   py("tests/test_house_style.py")),
 ("AI mode silently falls back to a stock photo", AI/"app/chains/template_designer.py",
   "            if not poster_imagery.is_configured():\n                raise TemplateGenerationError(",
   "            if False:\n                raise TemplateGenerationError(",
   py("tests/test_template_designer.py")),
 ("the booking QR is refused as an unapproved photo", AI/"app/chains/template_validator.py",
   "            is_brand = key in BRAND_PATHS", "            is_brand = False",
   py("tests/test_template_validator.py")),
 ("a GIF or PDF is accepted for upload in the browser", WEB/"src/lib/poster-photo.ts",
   "  if (!(ACCEPTED_PHOTO_TYPES as readonly string[]).includes(file.type)) {",
   "  if (!file.type.startsWith(\"image/\") && false) {",
   web("src/lib/poster-photo.test.ts")),
 ("an AI choice is sent even though AI is not set up", WEB/"src/lib/poster-photo.ts",
   "      if (!state.aiAvailable) return", "      if (false) return",
   web("src/lib/poster-photo.test.ts")),
 ("raw {{tokens}} shown on screen to a pharmacy that skipped onboarding", WEB/"src/lib/canvas/display-tokens.ts",
   "    out = out.replace(pattern, values?.[key] ?? \"\");", "    out = out.replace(pattern, values?.[key] || \"$&\");",
   web("src/lib/canvas/display-tokens.test.ts")),
 ("manage-as label outlives the session", WEB/"src/lib/server/cookies.ts",
   "    httpOnly: false,\n    sameSite: \"lax\",\n    secure: cookieSecure,\n    path: \"/\",\n    maxAge: seconds,",
   "    httpOnly: false,\n    sameSite: \"lax\",\n    secure: cookieSecure,\n    path: \"/\",\n    maxAge: 60 * 60 * 24 * 30,",
   web("src/lib/server/cookies.test.ts")),
 # ── security review, 3 October 2026 ──
 ("SEC-01 a poster can make the API fetch any URL", API/"src/Acme.Application/Posters/CanvasImageSources.cs",
   "        return uri.Scheme == Uri.UriSchemeHttps\n               && uri.IsDefaultPort\n               && AllowedHosts.Contains(uri.Host);",
   "        return true;",
   dotnet("FullyQualifiedName~SecurityReviewFixTests")),
 ("SEC-02 a poster can read another pharmacy's storage key", API/"src/Acme.Application/Posters/CanvasImageSources.cs",
   "        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false; // a raw storage key",
   "        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return true;",
   dotnet("FullyQualifiedName~SecurityReviewFixTests")),
 ("SEC-05 wrong passwords never lock an account", API/"src/Acme.Infrastructure/Security/MemoryLoginThrottle.cs",
   "        if (Failures(email, AddressKey(email, address)) >= ILoginThrottle.MaxFailuresPerAddress)",
   "        if (false)",
   dotnet("FullyQualifiedName~SecurityReviewFixTests")),
 ("SEC-10 an SVG that links outside itself is parsed on the server", API/"src/Acme.Infrastructure/Imaging/SvgRasterizer.cs",
   "        if (!IsSelfContained(text)) return null;\n", "",
   dotnet("FullyQualifiedName~SecurityReviewFixTests")),
 ("SEC-10 a non-image is stored as a logo", API/"src/Acme.Infrastructure/Storage/AzureBrandAssetService.cs",
   "            _ => (null, null),", "            _ => (\"image/png\", \"png\"),",
   dotnet("FullyQualifiedName~SecurityReviewFixTests")),
 ("SEC-13 a common password is accepted", API/"src/Acme.Application/Auth/PasswordPolicy.cs",
   "        if (plain.Length == 0 || Common.Contains(plain) || Common.Contains(unswapped))",
   "        if (plain.Length == 0)",
   dotnet("FullyQualifiedName~SecurityReviewFixTests")),
 ("SEC-08 classify-refine has no per-user limit", AI/"app/routers/video.py",
   "Depends(per_user_limit(\"classify-refine\", 20))", "Depends(get_current_user)",
   py("tests/test_security_review.py")),
 ("SEC-17 the model's error text reaches the caller", AI/"app/chains/template_designer.py",
   "                \"The design service could not be reached. Try again in a minute.\") from exc",
   "                f\"The design service could not be reached: {exc}\") from exc",
   py("tests/test_security_review.py")),
 # ── follow-up security review, 3 October 2026 ──
 ("R2-03 the caller's own X-Forwarded-For entry is passed to the API", WEB/"src/lib/server/forwarding.ts",
   "entries[entries.length - hopCount]", "entries[0]",
   web("src/lib/server/forwarding.test.ts")),
 ("R2-04 sign-in redirects off the site", WEB/"src/lib/safe-redirect.ts",
   [(r"  if (/[\\\u0000-\u001f\u007f]/.test(raw)) return null;" + "\n", ""),
    ("  if (url.origin !== base.origin) return null;\n", "")],
   None, web("src/lib/safe-redirect.test.ts")),
 ("R2-05 a differently-cased image key skips the check", API/"src/Acme.Application/Posters/CanvasImageSources.cs",
   "string.Equals(name, SourceProperty, StringComparison.OrdinalIgnoreCase)",
   "string.Equals(name, SourceProperty, StringComparison.Ordinal)",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("R2-05 an SVG nested in an SVG is not checked", API/"src/Acme.Infrastructure/Imaging/SvgRasterizer.cs",
   "\"(?!#|data:image/(?:png|jpe?g|webp|gif)[;,])\"", "\"(?!#|data:)\"",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("R2-06 the body's unsubscribe link is left blank", API/"src/Acme.Application/Messaging/IEmailSender.cs",
   "            ? links.PageUrl\n", "            ? string.Empty\n",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("R2-08 the account-wide lock also locks the owner out", API/"src/Acme.Infrastructure/Security/MemoryLoginThrottle.cs",
   "               && !cache.TryGetValue(KnownKey(email, address), out _);", "               && true;",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("R2-09 deactivation leaves issued tokens working", API/"src/Acme.Domain/Users/User.cs",
   "        IsActive = false;\n        EndAllSessions();", "        IsActive = false;\n        Touch();",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("R2-11 the JWT fingerprint answers in production", AI/"app/main.py",
   "    if not _docs:\n        raise HTTPException(status_code=404", "    if False:\n        raise HTTPException(status_code=404",
   py("tests/test_security_review.py")),
 ("R2-12 an oversized poster is accepted", API/"src/Acme.Application/Posters/CanvasLimits.cs",
   "        if (bytes > limits.MaxCanvasBytes)", "        if (false)",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("R2-13 every link type shares one key", API/"src/Acme.Application/Common/LinkSigningKeys.cs",
   "$\"marketing25/link/{purpose}/v1\"", "\"marketing25/link/v1\"",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("SEC-15 a design reads any pharmacy's upload", API/"src/Acme.Infrastructure/Templates/AiTemplateGenerator.cs",
   "await pharmacyPhotos.OpenAsync(ownerId, uploadName, ct)", "await pharmacyPhotos.OpenAsync(\"owner-1\", uploadName, ct)",
   dotnet("FullyQualifiedName~AiDesignPhotoTests")),
 ("SEC-15 photo uploads go to the public asset area", API/"src/Acme.Application/Ai/PosterPhotoService.cs",
   "new StoredPosterPhoto(PharmacyPhotos.UrlFor(fileName), contentType)", "new StoredPosterPhoto(\"/api/assets/templates/\" + fileName, contentType)",
   dotnet("FullyQualifiedName~AiDesignPhotoTests")),
 ("R2-15 email addresses written to logs in full", API/"src/Acme.Application/Common/PersonalData.cs",
   "        return shown + value[at..];", "        return value;",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
 ("R2-06 an unreadable consent cell is guessed", API/"src/Acme.Application/Patients/PatientCsv.cs",
   "            if (consentUnreadable)\n", "            if (false)\n",
   dotnet("FullyQualifiedName~SecurityFollowupFixTests")),
]


def _fails(runs) -> bool:
    return any(
        subprocess.run(cmd, cwd=cwd, capture_output=True, timeout=600).returncode != 0
        for cwd, cmd in runs
    )


def _restore_on_signal(signum, _frame):
    # Turn a termination into an exception so `finally` puts the file back.
    raise KeyboardInterrupt(f"signal {signum}")


def main() -> int:
    signal.signal(signal.SIGTERM, _restore_on_signal)
    only = [a for a in sys.argv[1:] if a != "--"] or None
    caught, missed, broken, unsound = [], [], [], []
    baseline_ok: dict[tuple, bool] = {}

    for name, path, find, replace, runs in M:
        if only and not any(o.lower() in name.lower() for o in only):
            continue

        # The tests must PASS on the real code first. Otherwise a wrong path, a
        # missing runner or an already-red test would fail below for reasons
        # unrelated to the bug, and be reported as "caught" — hiding a miss.
        key = tuple((str(cwd), tuple(cmd)) for cwd, cmd in runs)
        if key not in baseline_ok:
            baseline_ok[key] = not _fails(runs)
        if not baseline_ok[key]:
            print(f"  BASELINE {name}: its tests fail on the unmodified code — fix them first")
            unsound.append(name)
            continue

        original = path.read_text()
        pairs = find if isinstance(find, list) else [(find, replace)]
        bad = [f for f, _ in pairs if original.count(f) != 1]
        if bad:
            print(f"  ANCHOR   {name}: the code it targets has changed ({path.relative_to(ROOT)})")
            broken.append(name)
            continue
        mutated = original
        for f, r in pairs:
            mutated = mutated.replace(f, r)
        try:
            path.write_text(mutated)
            failed = _fails(runs)
        finally:
            path.write_text(original)
        (caught if failed else missed).append(name)
        print(f"  {'caught  ' if failed else 'MISSED  '} {name}")

    print(f"\n{len(caught)} caught, {len(missed)} missed, {len(broken)} stale anchors, "
          f"{len(unsound)} with failing baselines")
    if missed:
        print("A MISSED bug means its tests pass with the bug back in place. Strengthen them.")
    return 1 if missed or broken or unsound else 0


if __name__ == "__main__":
    sys.exit(main())
