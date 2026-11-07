"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  DISPLAY_CACHE_STORAGE_KEY,
  DISPLAY_CAPABILITIES_HEADER,
  DISPLAY_TOKEN_STORAGE_KEY,
  PAIRING_CODE_LENGTH,
  type KioskContent,
  type KioskSlide,
} from "@acme/shared-types";
import {
  KIOSK_CAPABILITIES,
  VIDEO_RETRY_AFTER_MS,
  advanceAfterMs,
  isVideo,
  playableSlides,
  posterImageUrls,
  slideKey,
} from "@/lib/kiosk-playlist";
import { KioskVideo } from "@/components/display/KioskVideo";

/**
 * A screen on a pharmacy wall.
 *
 * <b>This page is expected to run untouched for months.</b> That single fact
 * drives most of what follows, and it is what separates it from a slideshow:
 *
 * <ul>
 *   <li>It keeps the last playlist it managed to fetch, and the images with it.
 *       Pharmacy wifi drops. A screen that goes blank in a shop window because
 *       a router rebooted is worse than one showing yesterday's poster.</li>
 *   <li>It polls with an ETag rather than holding a socket open. Thirty seconds
 *       is fast enough that a pharmacist who edits a playlist sees it change
 *       while still standing there, and an unchanged answer costs nothing.</li>
 *   <li>It drifts the image by a few pixels each loop. An OLED or plasma panel
 *       showing an identical logo for a year will keep showing it after the
 *       screen is switched off.</li>
 *   <li>It never renders the Konva editor. Slides are pre-rendered PNGs — the
 *       same ones the campaign emails use — so a screen and an email cannot
 *       disagree about what a poster looks like.</li>
 *   <li>Videos play once, muted, letterboxed, then the loop moves on. They
 *       stream from a signed same-origin path and are never downloaded ahead
 *       of play: a playlist of MP4s would fill a cheap stick's storage. The
 *       browser's own HTTP cache may keep one it has played, so a video can
 *       survive a wifi drop; one it does not have is skipped.</li>
 * </ul>
 *
 * <b>No cookie is read or written.</b> The device holds one long random token
 * in local storage and sends it in a header. A cookie would ride along on every
 * request to this origin, which is a far wider grant than a device in a public
 * room should be carrying.
 */
export default function DisplayPage() {
  const [token, setToken] = useState<string | null>(null);
  const [content, setContent] = useState<KioskContent | null>(null);
  const [index, setIndex] = useState(0);
  const [images, setImages] = useState<Record<string, string>>({});
  // Videos that just failed to play, by slide key. Left out for a few minutes
  // and then tried again, so one bad stream does not blank a slot forever.
  const [failedVideos, setFailedVideos] = useState<ReadonlySet<string>>(() => new Set());
  const retryTimers = useRef<Set<ReturnType<typeof setTimeout>>>(new Set());
  const [offline, setOffline] = useState(false);
  const [booted, setBooted] = useState(false);

  // The ETag we last saw. A ref because the poll loop reads it without wanting
  // to be restarted every time it changes.
  const revision = useRef<string | null>(null);

  /* ── boot: token and last known playlist ──────────────────────────── */

  useEffect(() => {
    try {
      // A direct link arrives as `/display#token=…`.
      //
      // The fragment, never the query string — a fragment is not sent to the
      // server, so the credential stays out of every access log between the
      // device and the API. It is stored and then stripped from the address
      // bar, so the token is not left on screen for the next person to
      // photograph, and a browser restoring the tab does not re-apply a token
      // that has since been revoked.
      const hash = new URLSearchParams(window.location.hash.replace(/^#/, ""));
      const fromLink = hash.get("token");

      if (fromLink) {
        localStorage.setItem(DISPLAY_TOKEN_STORAGE_KEY, fromLink);
        history.replaceState(null, "", window.location.pathname);
      }

      setToken(fromLink ?? localStorage.getItem(DISPLAY_TOKEN_STORAGE_KEY));

      // Shown immediately, before the network is even tried. A screen that has
      // been unplugged and moved should light up with something rather than
      // sitting on a spinner while it waits for DNS.
      const cached = localStorage.getItem(DISPLAY_CACHE_STORAGE_KEY);
      if (cached) setContent(JSON.parse(cached) as KioskContent);
    } catch {
      // Private browsing, a full disk, a locked-down kiosk profile. None of
      // these should stop the screen working — it just cannot remember.
    }
    setBooted(true);
  }, []);

  /* ── fetching ─────────────────────────────────────────────────────── */

  const load = useCallback(async () => {
    if (!token) return;

    try {
      const headers: Record<string, string> = {
        "X-Display-Token": token,
        // Without this the API sends posters only — how a kiosk still running
        // pre-video code stays correct through a deploy.
        [DISPLAY_CAPABILITIES_HEADER]: KIOSK_CAPABILITIES,
      };
      if (revision.current) headers["If-None-Match"] = `"${revision.current}"`;

      const res = await fetch("/api/display/content", { headers, cache: "no-store" });

      if (res.status === 304) {
        setOffline(false);
        return;
      }

      if (res.status === 401) {
        // Revoked, or the screen was deleted. Forget the token and ask to be
        // paired again — the same response for every reason, because the
        // device cannot tell them apart and does not need to.
        try {
          localStorage.removeItem(DISPLAY_TOKEN_STORAGE_KEY);
          localStorage.removeItem(DISPLAY_CACHE_STORAGE_KEY);
        } catch {
          /* nothing to clean up */
        }
        setToken(null);
        setContent(null);
        return;
      }

      if (!res.ok) {
        // Anything else — including a 400 for a header that went missing in
        // transit — is treated as a network problem: keep showing the cached
        // playlist and try again on the next poll. Only a 401 costs the token,
        // because only a 401 means the token itself is finished.
        setOffline(true);
        return;
      }

      const next = (await res.json()) as KioskContent;
      revision.current = next.revision;
      setOffline(false);
      setContent(next);

      try {
        localStorage.setItem(DISPLAY_CACHE_STORAGE_KEY, JSON.stringify(next));
      } catch {
        /* the screen still works, it just will not survive a power cut */
      }
    } catch {
      // Almost always the network. Keep showing what we have.
      setOffline(true);
    }
  }, [token]);

  useEffect(() => {
    if (!token) return;
    void load();

    const seconds = content?.pollSeconds ?? 30;
    const id = setInterval(() => void load(), seconds * 1000);
    return () => clearInterval(id);
  }, [token, load, content?.pollSeconds]);

  /* ── images ───────────────────────────────────────────────────────── */

  /**
   * Slides are fetched rather than set as an `<img src>`, because the token
   * lives in a header. That is deliberate: a URL carrying the credential would
   * be written into every access log between here and the API.
   *
   * The object URLs are held for the life of the page, which is the life of the
   * screen. They are revoked only when a slide leaves the playlist.
   */
  useEffect(() => {
    if (!token || !content) return;
    let cancelled = false;

    // Posters only: a video streams when it plays and is never held here.
    const urls = posterImageUrls(content.slides);
    const wanted = new Set(urls);

    (async () => {
      for (const imageUrl of urls) {
        if (cancelled) return;
        if (images[imageUrl]) continue;

        try {
          const res = await fetch(imageUrl, {
            headers: { "X-Display-Token": token },
          });
          if (!res.ok) continue;

          const url = URL.createObjectURL(await res.blob());
          if (cancelled) {
            URL.revokeObjectURL(url);
            return;
          }
          setImages((prev) => ({ ...prev, [imageUrl]: url }));
        } catch {
          // Leave it out of the loop rather than showing a broken frame. The
          // next poll will try again.
        }
      }
    })();

    // Anything no longer on the playlist is released, or a screen that runs for
    // a year through a hundred seasonal posters slowly eats its own memory.
    setImages((prev) => {
      const kept: Record<string, string> = {};
      for (const [url, objectUrl] of Object.entries(prev)) {
        if (wanted.has(url)) kept[url] = objectUrl;
        else URL.revokeObjectURL(objectUrl);
      }
      return kept;
    });

    return () => {
      cancelled = true;
    };
    // `images` is deliberately not a dependency: it is written by this effect,
    // and depending on it would restart the fetch loop on every image that
    // arrives.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token, content]);

  /* ── the loop ─────────────────────────────────────────────────────── */

  /**
   * The slides that can go on screen: posters whose images have arrived, and
   * videos not sitting out after a failure.
   *
   * <b>Memoised, and that is load-bearing.</b> A plain `.filter()` returns a
   * new array on every render, and the advance timer below depends on it — so
   * every render cleared the pending timeout and started a fresh one. While
   * images were still arriving, each one re-set the clock, and a slow
   * connection could hold the first slide on screen indefinitely: the timer
   * would keep restarting faster than it could expire.
   *
   * The dependency is the revision, which images are loaded and which videos
   * are sitting out after a failure — all stable strings — so the array
   * identity changes only when the set of showable slides genuinely changes.
   */
  const readyKey = [
    content?.revision ?? "",
    Object.keys(images).sort().join(","),
    [...failedVideos].sort().join(","),
  ].join("|");
  const ready = useMemo(
    () => playableSlides(content?.slides ?? [], images, failedVideos),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [readyKey],
  );

  const advance = useCallback(() => setIndex((i) => i + 1), []);

  /** A video that would not play: skip it now, try it again in a few minutes. */
  const videoFailed = useCallback((key: string) => {
    setFailedVideos((prev) => new Set(prev).add(key));
    const timer = setTimeout(() => {
      retryTimers.current.delete(timer);
      setFailedVideos((prev) => {
        const next = new Set(prev);
        next.delete(key);
        return next;
      });
    }, VIDEO_RETRY_AFTER_MS);
    retryTimers.current.add(timer);
    // No index change: leaving the list is what moves the loop on. The slide
    // after the failed one slides into its position, so advancing as well
    // would skip that one.
  }, []);

  useEffect(() => {
    const timers = retryTimers.current;
    return () => timers.forEach(clearTimeout);
  }, []);

  useEffect(() => {
    if (ready.length === 0) return;

    // A poster's dwell time. For a video, only a safety net — it moves on
    // itself when it ends.
    const current = ready[index % ready.length];
    const id = setTimeout(advance, advanceAfterMs(current));

    return () => clearTimeout(id);
  }, [index, ready, advance]);

  /* ── keep the panel awake ─────────────────────────────────────────── */

  useEffect(() => {
    // Best effort. Not available on every browser, refused when the tab is
    // hidden, and released on its own when the device sleeps anyway — so it is
    // an improvement, never something to depend on. A screen still needs the
    // operating system's own sleep setting turned off.
    let lock: { release: () => Promise<void> } | null = null;

    const request = async () => {
      try {
        const nav = navigator as Navigator & {
          wakeLock?: { request: (t: "screen") => Promise<typeof lock> };
        };
        lock = (await nav.wakeLock?.request("screen")) ?? null;
      } catch {
        /* the pharmacy sets the TV's sleep timer instead */
      }
    };

    void request();
    const onVisible = () => {
      if (document.visibilityState === "visible") void request();
    };

    document.addEventListener("visibilitychange", onVisible);
    return () => {
      document.removeEventListener("visibilitychange", onVisible);
      void lock?.release().catch(() => {});
    };
  }, []);

  /* ── what is on screen ────────────────────────────────────────────── */

  if (!booted) return <Frame />;

  if (!token) {
    return <PairingScreen onPaired={(t) => setToken(t)} />;
  }

  if (ready.length === 0) {
    return (
      <Frame>
        <div className="text-center">
          <p className="text-3xl font-semibold text-white/90">
            {content?.screenName ?? "Screen ready"}
          </p>
          <p className="mt-3 text-lg text-white/50">
            {content && content.slides.length > 0
              ? "Loading…"
              : "Nothing assigned to this screen yet."}
          </p>
        </div>
        <StatusDot offline={offline} />
      </Frame>
    );
  }

  const slide = ready[index % ready.length];
  const playingVideo = isVideo(slide);

  // A few pixels of movement per slide. Invisible to a person, and enough to
  // stop a static logo etching itself into a panel over a year.
  const drift = index % 4;

  return (
    <Frame>
      {/*
        Every ready poster stays mounted and is faded between. Swapping the src
        of a single <img> shows a blank frame while the next one decodes, which
        on a wall reads as the screen glitching.
      */}
      {uniquePosters(ready).map((s) => (
        // eslint-disable-next-line @next/next/no-img-element
        <img
          key={slideKey(s)}
          src={images[s.imageUrl]}
          alt={s.name}
          className="absolute inset-0 h-full w-full object-contain transition-opacity duration-700"
          style={{
            opacity: !playingVideo && s.imageUrl === slide.imageUrl ? 1 : 0,
            transform: `translate(${drift}px, ${drift}px)`,
          }}
        />
      ))}

      {/*
        A video is mounted only for its turn — a dozen idle <video> elements
        would each hold a decoder and a buffer. Keyed on the turn as well as
        the video, so a playlist of one video still starts again from the top.
      */}
      {playingVideo && (
        <KioskVideo
          key={`${slideKey(slide)}#${index}`}
          slide={slide}
          drift={drift}
          onEnded={advance}
          onFailed={() => videoFailed(slideKey(slide))}
        />
      )}

      {/*
        Not over a video: its own captions sit at the bottom of the frame, and
        a strip over them would make them unreadable.
      */}
      {content?.addressLine && !playingVideo ? (
        <div className="pointer-events-none absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/70 to-transparent px-10 pb-7 pt-16">
          <p className="text-center text-2xl font-medium tracking-wide text-white/90">
            {content.addressLine}
          </p>
        </div>
      ) : null}

      <StatusDot offline={offline} />
    </Frame>
  );
}

/**
 * One `<img>` per poster image, even when a poster appears twice in the
 * playlist — two elements with one key is a React error, and the same picture
 * needs only one decoded copy.
 */
function uniquePosters(slides: readonly KioskSlide[]): KioskSlide[] {
  const seen = new Set<string>();
  return slides.filter((s) => {
    if (isVideo(s) || seen.has(s.imageUrl)) return false;
    seen.add(s.imageUrl);
    return true;
  });
}

/** Black, edge to edge, cursor hidden. A wall, not a web page. */
function Frame({ children }: { children?: React.ReactNode }) {
  return (
    <main className="relative h-screen w-screen overflow-hidden bg-black [&_*]:cursor-none">
      <div className="flex h-full w-full items-center justify-center">{children}</div>
    </main>
  );
}

/**
 * A small amber dot when the screen is running on its cached playlist.
 *
 * Deliberately tiny and in a corner. Somebody in the pharmacy should be able to
 * tell at a glance that a screen has lost its connection, without a customer
 * reading an error message over the counter.
 */
function StatusDot({ offline }: { offline: boolean }) {
  if (!offline) return null;
  return (
    <div
      className="absolute right-4 top-4 h-2.5 w-2.5 rounded-full bg-amber-400/80"
      title="Showing the last saved playlist — no connection"
    />
  );
}

/**
 * What an installer sees on a brand-new screen.
 *
 * Their whole job is to read six characters off a laptop and type them in, so
 * the code is the only thing on the page and the characters are enormous.
 */
function PairingScreen({ onPaired }: { onPaired: (token: string) => void }) {
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setPending(true);
    setError(null);

    try {
      const res = await fetch("/api/display/claim", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code: code.trim().toUpperCase() }),
      });

      const body = await res.json().catch(() => null);

      if (!res.ok) {
        setError(body?.error ?? "That code did not work.");
        return;
      }

      try {
        localStorage.setItem(DISPLAY_TOKEN_STORAGE_KEY, body.token);
      } catch {
        setError(
          "This browser will not let the screen remember it was paired. "
          + "Turn off private browsing and try again.",
        );
        return;
      }

      onPaired(body.token);
    } catch {
      setError("Could not reach Marketing25. Check the screen's connection.");
    } finally {
      setPending(false);
    }
  }

  return (
    <main className="flex h-screen w-screen items-center justify-center bg-black px-8">
      <form onSubmit={submit} className="w-full max-w-xl text-center">
        <h1 className="text-4xl font-semibold text-white">Pair this screen</h1>
        <p className="mt-3 text-lg text-white/60">
          On Marketing25, open Screens and read the pairing code for this display.
        </p>

        <input
          value={code}
          onChange={(e) => setCode(e.target.value.toUpperCase())}
          maxLength={PAIRING_CODE_LENGTH}
          autoFocus
          aria-label="Pairing code"
          placeholder="------"
          className="mt-10 w-full rounded-2xl border-2 border-white/20 bg-white/5 px-6 py-6 text-center font-mono text-6xl tracking-[0.3em] text-white placeholder:text-white/20 focus:border-white/50 focus:outline-none"
        />

        {error && (
          <p role="alert" className="mt-6 text-lg text-amber-300">
            {error}
          </p>
        )}

        <button
          type="submit"
          disabled={pending || code.trim().length !== PAIRING_CODE_LENGTH}
          className="mt-8 w-full rounded-xl bg-white px-6 py-4 text-xl font-semibold text-black disabled:opacity-30"
        >
          {pending ? "Pairing…" : "Pair screen"}
        </button>

        <p className="mt-8 text-sm text-white/40">
          The code lasts four hours and works once.
        </p>
      </form>
    </main>
  );
}
