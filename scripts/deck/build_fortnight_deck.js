/**
 * Builds the two-week progress deck for Sonar Marketing25.
 *
 * Stakeholder-framed: each slide leads with the capability delivered, with the
 * engineering underneath as supporting detail rather than the headline.
 *
 * Palette is "Teal Trust" from the pptx skill — it matches the product's own
 * --primary (185 72% 32%, a deep teal) and --brand-green, so the deck looks
 * like the thing it describes rather than generic corporate blue.
 */

const pptxgen = require("pptxgenjs");

// ── palette ──────────────────────────────────────────────────────────────
const INK = "0F2E33";        // near-black teal, dominant on dark slides
const TEAL = "0E7C86";       // primary
const SEAFOAM = "00A896";    // secondary
const MINT = "6FD8C4";       // light accent for dark backgrounds
const CLOUD = "F4F8F8";      // light slide background
const WHITE = "FFFFFF";
const MUTED = "5A7175";      // body text on light
const MUTED_DK = "9FBDC0";   // body text on dark
const AMBER = "D98324";      // single sharp accent, used sparingly

const TITLE_FONT = "Cambria";   // safe-list serif, renders true-to-width
const BODY_FONT = "Calibri";    // safe-list sans

const pres = new pptxgen();
pres.layout = "LAYOUT_WIDE";    // 13.3 x 7.5 — set BEFORE adding slides
pres.author = "Sonar Marketing25";
pres.title = "Two-Week Delivery Review";

const W = 13.3;
const M = 0.7;                  // outer margin
const CONTENT_W = W - M * 2;

/** Repeated motif: a filled circle holding a short label. */
function badge(slide, { x, y, size = 0.62, fill, text, textColor = WHITE, fontSize = 15 }) {
  slide.addShape(pres.ShapeType.ellipse, {
    x, y, w: size, h: size, fill: { color: fill },
  });
  slide.addText(text, {
    x, y, w: size, h: size,
    align: "center", valign: "middle",
    fontFace: BODY_FONT, fontSize, bold: true, color: textColor, margin: 0,
  });
}

function darkSlide() {
  const s = pres.addSlide();
  s.background = { color: INK };
  return s;
}

function lightSlide(title, kicker) {
  const s = pres.addSlide();
  s.background = { color: CLOUD };
  if (kicker) {
    s.addText(kicker.toUpperCase(), {
      x: M, y: 0.42, w: CONTENT_W, h: 0.26,
      fontFace: BODY_FONT, fontSize: 11, bold: true,
      color: SEAFOAM, charSpacing: 2, margin: 0,
    });
  }
  s.addText(title, {
    x: M, y: kicker ? 0.7 : 0.5, w: CONTENT_W, h: 0.72,
    fontFace: TITLE_FONT, fontSize: 34, bold: true, color: INK, margin: 0,
  });
  return s;
}

/** Icon-and-text row: the deck's repeated content unit. */
function row(slide, { x, y, w, mark, markFill, heading, body }) {
  badge(slide, { x, y: y + 0.04, size: 0.5, fill: markFill, text: mark, fontSize: 13 });
  slide.addText(heading, {
    x: x + 0.68, y, w: w - 0.68, h: 0.3,
    fontFace: BODY_FONT, fontSize: 15, bold: true, color: INK, margin: 0,
  });
  slide.addText(body, {
    x: x + 0.68, y: y + 0.3, w: w - 0.68, h: 0.62,
    fontFace: BODY_FONT, fontSize: 12, color: MUTED, margin: 0, valign: "top",
  });
}

/** Card with a subtle tint — no edge stripes. */
function card(slide, { x, y, w, h, fill = WHITE }) {
  slide.addShape(pres.ShapeType.roundRect, {
    x, y, w, h, rectRadius: 0.08,
    fill: { color: fill },
    shadow: { type: "outer", color: "1A3A3F", opacity: 0.1, blur: 10, offset: 2, angle: 90 },
  });
}

/* ─────────────────────────── 1. Title ─────────────────────────── */
{
  const s = darkSlide();

  s.addShape(pres.ShapeType.ellipse, {
    x: 9.6, y: -1.5, w: 5.4, h: 5.4, fill: { color: TEAL, transparency: 62 },
  });
  s.addShape(pres.ShapeType.ellipse, {
    x: 11.2, y: 3.6, w: 3.2, h: 3.2, fill: { color: SEAFOAM, transparency: 76 },
  });

  s.addText("SONAR MARKETING25", {
    x: M, y: 1.85, w: 8.4, h: 0.3,
    fontFace: BODY_FONT, fontSize: 12, bold: true, color: MINT, charSpacing: 3, margin: 0,
  });
  s.addText("Two Weeks of Delivery", {
    x: M, y: 2.25, w: 8.6, h: 1.5,
    fontFace: TITLE_FONT, fontSize: 46, bold: true, color: WHITE, margin: 0,
  });
  s.addText(
    "From a manual, developer-dependent workflow to a platform " +
    "the pharmacy team runs themselves.",
    {
      x: M, y: 3.75, w: 7.9, h: 0.9,
      fontFace: BODY_FONT, fontSize: 16, color: MUTED_DK, margin: 0,
    },
  );

  const stats = [
    ["3", "major releases"],
    ["24", "poster templates"],
    ["50+", "automated checks"],
  ];
  stats.forEach(([n, label], i) => {
    const x = M + i * 2.5;
    s.addText(n, {
      x, y: 5.15, w: 2.2, h: 0.75,
      fontFace: TITLE_FONT, fontSize: 40, bold: true, color: MINT, margin: 0,
    });
    s.addText(label, {
      x, y: 5.88, w: 2.2, h: 0.3,
      fontFace: BODY_FONT, fontSize: 11, color: MUTED_DK, margin: 0,
    });
  });

  s.addNotes(
    "Two-week review. The through-line: the pharmacy team can now do things " +
    "themselves that previously needed a developer — designing poster " +
    "templates, managing accounts, and seeing how the platform is used.",
  );
}

/* ─────────────────────── 2. Executive summary ─────────────────── */
{
  const s = lightSlide("What changed", "Summary");

  const items = [
    ["01", TEAL, "Posters design themselves",
      "Describe a poster in a sentence and the platform produces a print-ready design, with photography, in about a minute."],
    ["02", SEAFOAM, "A complete admin console",
      "Accounts, pharmacies, subscriptions, templates and a full audit trail — with role-based access and bulk actions."],
    ["03", TEAL, "Visibility into usage",
      "Platform analytics showing what's being produced, by whom, and how that trends over time."],
    ["04", SEAFOAM, "Revenue infrastructure",
      "Stripe billing end to end: checkout, plan tiers, credit packs and webhook-driven upgrades."],
  ];

  items.forEach(([mark, fill, heading, body], i) => {
    const col = i % 2;
    const rowIdx = Math.floor(i / 2);
    row(s, {
      x: M + col * 6.15,
      y: 1.85 + rowIdx * 1.75,
      w: 5.75,
      mark, markFill: fill, heading, body,
    });
  });

  s.addNotes(
    "Four headlines. The first is the biggest shift — poster design was a " +
    "developer task, now it's self-service.",
  );
}

/* ────────────────── 3. AI poster generation ───────────────────── */
{
  const s = lightSlide("Poster templates, on demand", "Release 1");

  s.addText(
    "Previously every new poster design required a developer to hand-write it. " +
    "Admins now describe what they want and publish the result themselves.",
    {
      x: M, y: 1.6, w: 7.1, h: 0.75,
      fontFace: BODY_FONT, fontSize: 13.5, color: MUTED, margin: 0,
    },
  );

  const steps = [
    ["Describe", "A sentence: the service, the audience, the mood."],
    ["Illustrate", "Upload a photo, or generate one — stock photography by default."],
    ["Review", "Full-size preview before anything is published."],
    ["Publish", "Live to every pharmacy, or refine it in the designer first."],
  ];
  steps.forEach(([h, b], i) => {
    row(s, {
      x: M, y: 2.5 + i * 0.92, w: 6.9,
      mark: String(i + 1), markFill: i % 2 ? SEAFOAM : TEAL,
      heading: h, body: b,
    });
  });

  card(s, { x: 8.15, y: 1.55, w: 4.45, h: 4.15 });
  s.addText("Quality is enforced, not hoped for", {
    x: 8.45, y: 1.8, w: 3.85, h: 0.55,
    fontFace: BODY_FONT, fontSize: 14, bold: true, color: INK, margin: 0,
  });
  s.addText(
    [
      { text: "Every generated design is checked against print rules before it can be published:", options: { breakLine: true, paraSpaceAfter: 8 } },
      { text: "Text stays on the page and clear of the trim edge", options: { bullet: true, breakLine: true } },
      { text: "Nothing is hidden behind an opaque shape", options: { bullet: true, breakLine: true } },
      { text: "Contrast is readable at distance", options: { bullet: true, breakLine: true } },
      { text: "Photographs are never stretched out of shape", options: { bullet: true, breakLine: true } },
      { text: "The supplied photograph actually appears", options: { bullet: true, breakLine: true } },
      { text: "Imagery comes from a vetted library", options: { bullet: true } },
    ],
    {
      x: 8.45, y: 2.4, w: 3.85, h: 2.6,
      fontFace: BODY_FONT, fontSize: 11.5, color: MUTED, margin: 0, paraSpaceAfter: 5,
      valign: "top",
    },
  );
  s.addText("Failures are corrected automatically and re-checked.", {
    x: 8.45, y: 5.02, w: 3.85, h: 0.5,
    fontFace: BODY_FONT, fontSize: 11, italic: true, color: TEAL, margin: 0,
  });

  s.addNotes(
    "The validator is the important part. It's calibrated against the 24 " +
    "hand-authored templates, so it accepts the house style and rejects real " +
    "defects rather than fighting the designers.",
  );
}

/* ──────────────────── 4. Admin console ────────────────────────── */
{
  const s = lightSlide("A console for running the business", "Release 2");

  const areas = [
    ["Overview", "Account mix, plan distribution, output and estimated revenue at a glance."],
    ["Users", "Search, filter and sort every account; full detail and every action in one place."],
    ["Pharmacies", "Customer accounts with their assigned manager, onboarding state and plan."],
    ["Subscriptions", "Plan distribution and manual overrides for comps and failed webhooks."],
    ["Templates", "Design, publish, duplicate and retire the shared poster gallery."],
    ["Audit log", "Every administrative action, append-only, visible to all staff."],
  ];

  areas.forEach(([h, b], i) => {
    const col = i % 3;
    const rowIdx = Math.floor(i / 3);
    const x = M + col * 4.1;
    const y = 1.75 + rowIdx * 2.35;
    card(s, { x, y, w: 3.8, h: 1.95 });
    badge(s, { x: x + 0.3, y: y + 0.26, size: 0.44, fill: rowIdx ? SEAFOAM : TEAL, text: "", fontSize: 1 });
    s.addText(h, {
      x: x + 0.88, y: y + 0.28, w: 2.7, h: 0.4,
      fontFace: BODY_FONT, fontSize: 15, bold: true, color: INK, margin: 0,
    });
    s.addText(b, {
      x: x + 0.3, y: y + 0.82, w: 3.2, h: 1.0,
      fontFace: BODY_FONT, fontSize: 11.5, color: MUTED, margin: 0, valign: "top",
    });
  });

  s.addNotes(
    "Six areas. Access is role-based: administrators manage every pharmacy " +
    "account, but only a super admin can create or change other staff.",
  );
}

/* ─────────────── 5. Safeguards / governance ───────────────────── */
{
  const s = darkSlide();

  s.addText("GOVERNANCE", {
    x: M, y: 0.55, w: CONTENT_W, h: 0.26,
    fontFace: BODY_FONT, fontSize: 11, bold: true, color: MINT, charSpacing: 2, margin: 0,
  });
  s.addText("Powerful, but not dangerous", {
    x: M, y: 0.85, w: CONTENT_W, h: 0.7,
    fontFace: TITLE_FONT, fontSize: 34, bold: true, color: WHITE, margin: 0,
  });
  s.addText(
    "Administrators can act on any pharmacy account. The rules that keep that safe " +
    "are enforced by the server, not just hidden in the interface.",
    {
      x: M, y: 1.65, w: 8.6, h: 0.6,
      fontFace: BODY_FONT, fontSize: 13.5, color: MUTED_DK, margin: 0,
    },
  );

  const rules = [
    "Only a super admin can create or modify staff accounts",
    "The last active super admin cannot be demoted",
    "Nobody can change their own role or deactivate themselves",
    "An admin with pharmacies assigned cannot be demoted until they're reassigned",
    "Bulk actions apply the same rules to every account, one at a time",
    "The audit trail is append-only — there is no delete",
  ];
  rules.forEach((t, i) => {
    const col = i % 2;
    const rowIdx = Math.floor(i / 2);
    const x = M + col * 6.15;
    const y = 2.6 + rowIdx * 0.98;
    badge(s, { x, y, size: 0.34, fill: SEAFOAM, text: "", fontSize: 1 });
    s.addText(t, {
      x: x + 0.52, y: y + 0.03, w: 5.5, h: 0.68,
      fontFace: BODY_FONT, fontSize: 12.5, color: WHITE, margin: 0, valign: "top",
    });
  });

  s.addText(
    "Deactivating an account blocks sign-in immediately and deletes nothing — " +
    "posters, campaigns and history are retained.",
    {
      x: M, y: 5.55, w: 11.5, h: 0.5,
      fontFace: BODY_FONT, fontSize: 11.5, italic: true, color: MINT, margin: 0,
    },
  );

  s.addNotes(
    "Worth stressing that these are server-side. The UI disables what it can, " +
    "but the API refuses regardless of what the client sends.",
  );
}

/* ──────────────────── 6. Analytics ────────────────────────────── */
{
  const s = lightSlide("Seeing how the platform is used", "Release 3");

  s.addText(
    "Platform-wide activity over a rolling window, with the busiest accounts ranked.",
    {
      x: M, y: 1.62, w: 7.5, h: 0.4,
      fontFace: BODY_FONT, fontSize: 13.5, color: MUTED, margin: 0,
    },
  );

  s.addChart(
    pres.ChartType.line,
    [
      { name: "Posters", labels: ["W1 M", "T", "W", "T", "F", "W2 M", "T", "W", "T", "F"],
        values: [4, 7, 6, 9, 12, 10, 14, 13, 17, 19] },
      { name: "Videos", labels: ["W1 M", "T", "W", "T", "F", "W2 M", "T", "W", "T", "F"],
        values: [1, 2, 2, 3, 4, 3, 5, 4, 6, 7] },
      { name: "Campaigns", labels: ["W1 M", "T", "W", "T", "F", "W2 M", "T", "W", "T", "F"],
        values: [0, 1, 1, 2, 3, 2, 4, 3, 5, 6] },
    ],
    {
      x: M, y: 2.2, w: 7.6, h: 3.9,
      chartColors: [TEAL, SEAFOAM, MINT],
      showTitle: true, title: "Output per day (illustrative)",
      titleFontFace: BODY_FONT, titleFontSize: 12, titleColor: MUTED,
      showLegend: true, legendPos: "b", legendFontSize: 10, legendColor: MUTED,
      catAxisLabelColor: MUTED, valAxisLabelColor: MUTED,
      catAxisLabelFontSize: 9, valAxisLabelFontSize: 9,
      valGridLine: { color: "DCE7E7", size: 1 },
      catGridLine: { style: "none" },
      lineDataSymbol: "none", lineSize: 3, lineSmooth: true,
    },
  );

  card(s, { x: 8.6, y: 2.2, w: 4.0, h: 3.9 });
  s.addText("What it answers", {
    x: 8.9, y: 2.45, w: 3.4, h: 0.4,
    fontFace: BODY_FONT, fontSize: 14, bold: true, color: INK, margin: 0,
  });
  s.addText(
    [
      { text: "Is usage growing or flat?", options: { bullet: true, breakLine: true } },
      { text: "Which pharmacies are most active?", options: { bullet: true, breakLine: true } },
      { text: "Are campaigns actually being sent?", options: { bullet: true, breakLine: true } },
      { text: "Which accounts may need support?", options: { bullet: true } },
    ],
    {
      x: 8.9, y: 2.95, w: 3.4, h: 1.8,
      fontFace: BODY_FONT, fontSize: 12, color: MUTED, margin: 0, paraSpaceAfter: 7,
      valign: "top",
    },
  );
  s.addText(
    "Quiet days are shown as quiet, not skipped — so a gap reads as a gap.",
    {
      x: 8.9, y: 5.15, w: 3.4, h: 0.75,
      fontFace: BODY_FONT, fontSize: 11, italic: true, color: TEAL, margin: 0,
    },
  );

  s.addNotes(
    "Chart figures are illustrative — the shape of the feature, not real data. " +
    "Campaigns count on the day they were sent, and drafts never appear.",
  );
}

/* ─────────────── 7. Earlier in the fortnight ──────────────────── */
{
  const s = lightSlide("Also delivered", "Foundations");

  const groups = [
    [TEAL, "Billing", [
      "Stripe checkout, monthly and annual plans",
      "Credit packs and webhook-driven upgrades",
      "Billing portal and plan gating",
    ]],
    [SEAFOAM, "Design system", [
      "24 poster templates, 12 newly authored",
      "A4 and A3 page sizes with proportional resize",
      "Sidebar navigation replacing the header menu",
    ]],
    [TEAL, "Reliability", [
      "Idle-only sign-out — active users are never logged out",
      "Poster emails now carry the complete design",
      "Video generation fixed for custom scripts",
    ]],
  ];

  groups.forEach(([fill, heading, lines], i) => {
    const x = M + i * 4.1;
    card(s, { x, y: 1.75, w: 3.8, h: 2.85 });
    badge(s, { x: x + 0.3, y: 2.05, size: 0.5, fill, text: String(i + 1), fontSize: 13 });
    s.addText(heading, {
      x: x + 0.95, y: 2.1, w: 2.6, h: 0.4,
      fontFace: BODY_FONT, fontSize: 16, bold: true, color: INK, margin: 0,
    });
    s.addText(
      lines.map((t, j) => ({
        text: t,
        options: { bullet: true, breakLine: j < lines.length - 1 },
      })),
      {
        x: x + 0.3, y: 2.75, w: 3.2, h: 1.95,
        fontFace: BODY_FONT, fontSize: 11.5, color: MUTED, margin: 0, paraSpaceAfter: 8,
        valign: "top",
      },
    );
  });

  s.addNotes(
    "The first week was mostly billing and the design system; the second was " +
    "the admin console and AI generation on top of it.",
  );
}

/* ─────────────────── 8. Quality & testing ─────────────────────── */
{
  const s = darkSlide();

  s.addText("QUALITY", {
    x: M, y: 0.55, w: CONTENT_W, h: 0.26,
    fontFace: BODY_FONT, fontSize: 11, bold: true, color: MINT, charSpacing: 2, margin: 0,
  });
  s.addText("Catching problems before you do", {
    x: M, y: 0.85, w: CONTENT_W, h: 0.7,
    fontFace: TITLE_FONT, fontSize: 34, bold: true, color: WHITE, margin: 0,
  });
  s.addText(
    "Several issues this fortnight were found by manual testing rather than by " +
    "the build. That gap is now closed with automated checks that run in seconds.",
    {
      x: M, y: 1.65, w: 8.8, h: 0.6,
      fontFace: BODY_FONT, fontSize: 13.5, color: MUTED_DK, margin: 0,
    },
  );

  const suites = [
    ["End-to-end", "One command exercises the whole journey: sign-in, admin, image generation, publishing, PDF export."],
    ["Print rules", "27 tests, validated against every hand-authored template so the rules match the house style."],
    ["Domain", "Business rules and the URL handling that silently broke poster images."],
    ["Types", "Full type checking across the web app and shared contracts."],
  ];
  suites.forEach(([h, b], i) => {
    const col = i % 2;
    const rowIdx = Math.floor(i / 2);
    const x = M + col * 6.15;
    const y = 2.6 + rowIdx * 1.65;
    badge(s, { x, y: y + 0.04, size: 0.5, fill: i % 2 ? SEAFOAM : TEAL, text: String(i + 1), fontSize: 13 });
    s.addText(h, {
      x: x + 0.68, y, w: 5.3, h: 0.34,
      fontFace: BODY_FONT, fontSize: 15, bold: true, color: WHITE, margin: 0,
    });
    s.addText(b, {
      x: x + 0.68, y: y + 0.36, w: 5.3, h: 0.95,
      fontFace: BODY_FONT, fontSize: 12, color: MUTED_DK, margin: 0, valign: "top",
    });
  });

  s.addText(
    "Each check exists because that exact thing reached manual testing at least once.",
    {
      x: M, y: 6.1, w: 11.5, h: 0.5,
      fontFace: BODY_FONT, fontSize: 11.5, italic: true, color: MINT, margin: 0,
    },
  );

  s.addNotes(
    "Be candid here: the fortnight had real back-and-forth on bugs that only " +
    "showed in manual testing. The test suite is the response to that.",
  );
}

/* ───────────────────── 9. Where things stand ──────────────────── */
{
  const s = lightSlide("Where things stand", "Status");

  const cols = [
    ["Live", TEAL, [
      "AI poster generation with imagery",
      "Admin console and audit trail",
      "Platform analytics",
      "Stripe billing",
      "24 poster templates",
    ]],
    ["Needs a decision", AMBER, [
      "AI image generation is off — stock photography is the default",
      "Email open and click tracking is estimated, not measured",
      "Manual plan overrides are temporary on Stripe-billed accounts",
    ]],
    ["Next", SEAFOAM, [
      "Browser-level automated testing",
      "Per-pharmacy analytics for the Pro tier",
      "Detecting templates whose imagery has gone missing",
    ]],
  ];

  cols.forEach(([heading, fill, lines], i) => {
    const x = M + i * 4.1;
    card(s, { x, y: 1.75, w: 3.8, h: 3.15 });
    badge(s, { x: x + 0.3, y: 2.05, size: 0.5, fill, text: "", fontSize: 1 });
    s.addText(heading, {
      x: x + 0.95, y: 2.1, w: 2.6, h: 0.4,
      fontFace: BODY_FONT, fontSize: 16, bold: true, color: INK, margin: 0,
    });
    s.addText(
      lines.map((t, j) => ({
        text: t,
        options: { bullet: true, breakLine: j < lines.length - 1 },
      })),
      {
        x: x + 0.3, y: 2.75, w: 3.2, h: 2.25,
        fontFace: BODY_FONT, fontSize: 11.5, color: MUTED, margin: 0, paraSpaceAfter: 8,
        valign: "top",
      },
    );
  });

  s.addNotes(
    "The middle column is the one to discuss — each item is a choice rather " +
    "than an outstanding task.",
  );
}

/* ────────────────────── 10. Closing ───────────────────────────── */
{
  const s = darkSlide();

  s.addShape(pres.ShapeType.ellipse, {
    x: -1.6, y: 3.4, w: 5.0, h: 5.0, fill: { color: TEAL, transparency: 68 },
  });
  s.addShape(pres.ShapeType.ellipse, {
    x: 10.8, y: -1.2, w: 4.2, h: 4.2, fill: { color: SEAFOAM, transparency: 78 },
  });

  s.addText("The shift", {
    x: M, y: 2.15, w: 9, h: 0.4,
    fontFace: BODY_FONT, fontSize: 12, bold: true, color: MINT, charSpacing: 3, margin: 0,
  });
  s.addText(
    "Work that needed a developer now takes\na pharmacy administrator a minute.",
    {
      x: M, y: 2.65, w: 10.2, h: 1.7,
      fontFace: TITLE_FONT, fontSize: 34, bold: true, color: WHITE, margin: 0, lineSpacingMultiple: 1.2,
    },
  );
  s.addText(
    "Designing a poster, onboarding a pharmacy, changing a plan, checking how the " +
    "platform is being used — all self-service, all recorded, all reversible.",
    {
      x: M, y: 4.5, w: 9.4, h: 0.9,
      fontFace: BODY_FONT, fontSize: 15, color: MUTED_DK, margin: 0,
    },
  );

  s.addNotes("Close on the capability shift rather than the feature list.");
}

pres.writeFile({ fileName: process.argv[2] || "sonar-fortnight-review.pptx" })
  .then((f) => console.log("wrote", f));
