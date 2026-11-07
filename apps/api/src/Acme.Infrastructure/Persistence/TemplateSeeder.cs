using Acme.Application.Audit;
using Acme.Application.Templates;
using Acme.Domain.Templates;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Persistence;

public sealed partial class TemplateSeeder(
    ITemplateRepository repo,
    IBlobStorageService blobs,
    IAuditLogger audit,
    ILogger<TemplateSeeder> logger) : ITemplateSeeder
{
    // Captured as fields so TemplateSeeder.House.cs, the other half of this
    // partial class, uses them without depending on primary-constructor
    // parameters being visible across partial declarations.
    private readonly IBlobStorageService _blobs = blobs;
    private readonly ILogger<TemplateSeeder> _logger = logger;

    /// <summary>
    /// Insert any built-in template that isn't in the database yet.
    /// </summary>
    /// <remarks>
    /// Insert-only, deliberately. This used to upsert, which meant the code was
    /// the source of truth and the database was a cache: an admin could edit a
    /// built-in template, see the change persist, and then watch it silently
    /// revert on the next deploy or container restart.
    ///
    /// Now the database owns templates once they exist. The seeder's job is to
    /// populate a fresh environment, not to police an existing one — so adding
    /// a new template to <see cref="DefaultTemplates"/> still ships it, but
    /// editing an existing one in code will NOT reach an environment that has
    /// already seeded it. Change those through the admin UI, or delete the row
    /// first if you really want the code version back.
    /// </remarks>
    public async Task SeedIfEmptyAsync(CancellationToken ct)
    {
        foreach (var t in DefaultTemplates())
        {
            var existing = await repo.FindByIdAsync(t.Id, ct);
            if (existing is not null) continue;

            // A template whose photographs are not in storage yet is held back,
            // not inserted with holes in it — see HouseTemplates.
            if (!await EnsureAssetsAsync(t.Id, ct)) continue;

            await repo.AddAsync(t, ct);
            await audit.RecordAsync(
                AuditEvent.Create(AuditActor.System("template-seeder"), "system.seed.template",
                    "template", t.Id, AuditResults.Ok),
                ct);
        }
    }

    /* ------------------------------------------------------------------ *
     * Release dates
     *
     * The seeder reconstructs every template on each startup, so their
     * CreatedAtUtc would otherwise be "whenever the process last booted" —
     * identical across all of them, leaving the gallery's newest-first order
     * to fall out of yield order rather than anything meaningful.
     *
     * Fixed dates make the ordering deterministic and survive restarts. Bump
     * a batch date when you add a set you want at the top.
     * ------------------------------------------------------------------ */
    private static readonly DateTime OriginalSetDate =
        new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ModernSetDate =
        new(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PhotographicSetDate =
        new(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The built-in templates, for tests that assert properties of what ships.
    /// </summary>
    /// <remarks>
    /// Exposed rather than making <see cref="DefaultTemplates"/> public, so the
    /// only supported way to get these outside the seeder is one that says in
    /// its name that it is for tests. They shipped with invented phone numbers
    /// and addresses once; <c>TemplateContactDetailsTests</c> is what stops that
    /// happening again, and it needs to read them.
    /// </remarks>
    public static IEnumerable<Template> DefaultTemplatesForTesting() => DefaultTemplates();

    private static IEnumerable<Template> DefaultTemplates()
    {
        // ---- House-style set (newest) ----
        // Generated designs with baked photographs; see TemplateSeeder.House.cs.
        foreach (var house in HouseTemplates())
            yield return house;

        // ---- Photographic set ----
        // Tagged "photo" as well as "modern" so the gallery can offer
        // "with photography" as a filter — some pharmacies want imagery,
        // others want fully recolourable vector-only designs.

        yield return Template.CreateWithId(
            "sys-vaccination-clinic",
            "Vaccination Clinic Open",
            "Full-bleed photo with a dark scrim and a white schedule panel. For advertising clinic hours.",
            TemplateCategory.Vaccination,
            new[] { "modern", "photo", "vaccination", "clinic", "opening hours" },
            VaccinationClinicCanvas(),
            createdAtUtc: PhotographicSetDate);

        yield return Template.CreateWithId(
            "sys-ask-pharmacist",
            "Ask Your Pharmacist",
            "Full-page duotone photograph with oversized type. Bold window or wall poster.",
            TemplateCategory.General,
            new[] { "modern", "photo", "duotone", "advice", "consultation" },
            AskPharmacistCanvas(),
            createdAtUtc: PhotographicSetDate);

        yield return Template.CreateWithId(
            "sys-diabetes-screening",
            "Diabetes Risk Check",
            "Split layout with a tinted photo panel and eligibility pills.",
            TemplateCategory.Awareness,
            new[] { "modern", "photo", "diabetes", "screening", "risk check" },
            DiabetesScreeningCanvas(),
            createdAtUtc: PhotographicSetDate);

        yield return Template.CreateWithId(
            "sys-child-health",
            "Child Health Clinic",
            "Soft, reassuring layout with a framed photo card. Aimed at parents and carers.",
            TemplateCategory.General,
            new[] { "modern", "photo", "children", "parents", "advice" },
            ChildHealthClinicCanvas(),
            createdAtUtc: PhotographicSetDate);

        yield return Template.CreateWithId(
            "sys-mens-health",
            "Men's Health MOT",
            "High-contrast dark editorial with a numbered checklist and a photo footer band.",
            TemplateCategory.Awareness,
            new[] { "modern", "photo", "mens health", "screening", "health check" },
            MensHealthCanvas(),
            createdAtUtc: PhotographicSetDate);

        yield return Template.CreateWithId(
            "sys-inhaler-review",
            "Inhaler Technique Review",
            "Duotone header with a statistic-led headline and a three-step explainer.",
            TemplateCategory.Awareness,
            new[] { "modern", "photo", "asthma", "inhaler", "respiratory" },
            AsthmaReviewCanvas(),
            createdAtUtc: PhotographicSetDate);

        // ---- Modern set ----
        // Tagged "modern" so the templates gallery can surface them together
        // and so a future filter has something stable to key on.

        yield return Template.CreateWithId(
            "sys-blood-pressure",
            "Blood Pressure Check",
            "Bold diagonal layout with a target-reading badge. For promoting free in-store BP checks.",
            TemplateCategory.Awareness,
            new[] { "modern", "blood pressure", "heart", "screening", "free" },
            BloodPressureCanvas(),
            createdAtUtc: ModernSetDate);

        yield return Template.CreateWithId(
            "sys-winter-wellness",
            "Winter Wellness Offer",
            "Dark editorial poster with a discount badge and a four-card service grid.",
            TemplateCategory.Seasonal,
            new[] { "modern", "winter", "offer", "promotion", "flu" },
            WinterWellnessCanvas(),
            createdAtUtc: ModernSetDate);

        yield return Template.CreateWithId(
            "sys-pharmacy-first",
            "Pharmacy First Services",
            "Clean condition list on pill rows. For advertising treat-and-assess services.",
            TemplateCategory.General,
            new[] { "modern", "pharmacy first", "services", "nhs", "minor ailments" },
            PharmacyFirstCanvas(),
            createdAtUtc: ModernSetDate);

        yield return Template.CreateWithId(
            "sys-menopause-support",
            "Menopause Support",
            "Warm, calm layout for a sensitive subject. Leads with reassurance rather than symptoms.",
            TemplateCategory.Awareness,
            new[] { "modern", "menopause", "womens health", "hrt", "support" },
            MenopauseSupportCanvas(),
            createdAtUtc: ModernSetDate);

        yield return Template.CreateWithId(
            "sys-medicine-delivery",
            "Free Prescription Delivery",
            "High-contrast layout with angled stripes and a numbered three-step explainer.",
            TemplateCategory.Promotion,
            new[] { "modern", "delivery", "repeat prescription", "free", "service" },
            MedicineDeliveryCanvas(),
            createdAtUtc: ModernSetDate);

        yield return Template.CreateWithId(
            "sys-new-year-health",
            "New Year Health Check",
            "Split-panel editorial design with a vertical year mark and a four-metric card grid.",
            TemplateCategory.Seasonal,
            new[] { "modern", "new year", "health check", "screening", "january" },
            NewYearHealthCanvas(),
            createdAtUtc: ModernSetDate);

        // ---- Original set ----

        yield return Template.CreateWithId(
            "sys-flu-vaccination",
            "Flu Vaccination Reminder",
            "Bold A4 reminder that your pharmacy is offering flu shots.",
            TemplateCategory.Vaccination,
            new[] { "flu", "vaccine", "reminder", "seasonal" },
            FluVaccinationCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-vitamin-d",
            "Vitamin D Awareness",
            "Educational poster on the signs of Vitamin D deficiency.",
            TemplateCategory.Awareness,
            new[] { "vitamin", "wellness", "education" },
            VitaminDCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-loyalty-rewards",
            "Loyalty Rewards Promo",
            "High-impact dark theme announcing a loyalty program.",
            TemplateCategory.Promotion,
            new[] { "loyalty", "promo", "rewards" },
            LoyaltyRewardsCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-hand-hygiene",
            "Hand Hygiene Steps",
            "Step-by-step poster on the 20-second handwashing technique.",
            TemplateCategory.Safety,
            new[] { "hygiene", "safety", "infection-control" },
            HandHygieneCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-allergy-season",
            "Spring Allergy Survival Kit",
            "Seasonal poster grouping antihistamines, sprays, and eye drops.",
            TemplateCategory.Seasonal,
            new[] { "allergy", "spring", "tips" },
            AllergySeasonCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-welcome",
            "New Patient Welcome",
            "Friendly welcome poster with three service highlights.",
            TemplateCategory.General,
            new[] { "welcome", "intro", "services" },
            WelcomeCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-covid-booster",
            "Autumn COVID-19 Booster",
            "Clean clinical reminder for the autumn COVID-19 booster campaign.",
            TemplateCategory.Vaccination,
            new[] { "covid", "booster", "winter", "nhs" },
            CovidBoosterCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-diabetes-awareness",
            "Diabetes Awareness",
            "Know-the-signs poster with a free in-pharmacy check offer.",
            TemplateCategory.Awareness,
            new[] { "diabetes", "screening", "wellness" },
            DiabetesAwarenessCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-repeat-prescription",
            "Repeat Prescription Service",
            "Three-step explainer for your repeat prescription service.",
            TemplateCategory.General,
            new[] { "prescription", "repeat", "service" },
            RepeatPrescriptionCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-travel-clinic",
            "Travel Health Clinic",
            "Warm travel-themed poster covering vaccines, antimalarials, and kits.",
            TemplateCategory.Promotion,
            new[] { "travel", "clinic", "vaccine", "malaria" },
            TravelClinicCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-stop-smoking",
            "Stop Smoking Support",
            "Confident, supportive poster for your stop-smoking service.",
            TemplateCategory.Awareness,
            new[] { "smoking", "cessation", "support", "nrt" },
            StopSmokingCanvas(),
            createdAtUtc: OriginalSetDate);

        yield return Template.CreateWithId(
            "sys-childrens-cough-cold",
            "Children's Cough & Cold",
            "Safe-relief guidance and pharmacist red-flag advice for under-12s.",
            TemplateCategory.Seasonal,
            new[] { "children", "cough", "cold", "winter" },
            ChildrensCoughColdCanvas(),
            createdAtUtc: OriginalSetDate);
    }

    /* ------------------------------------------------------------------ *
     * Modern set
     *
     * A newer visual language than the originals: full-bleed diagonal bands,
     * oversized headlines, badge circles, and pill-shaped service chips.
     *
     * Built entirely from rect / circle / line / text because those are the
     * only primitives all three renderers agree on — the Konva editor, the
     * QuestPDF exporter, and the Python video pipeline. Images stay
     * rectangular panels: the schema has no masking, so a "circular photo"
     * would render as a circle in the browser and a square in the PDF.
     * Geometry carries the styling instead, which also keeps every template
     * fully recolourable from the editor.
     * ------------------------------------------------------------------ */

    /* ------------------------------------------------------------------ *
     * Photographic set
     *
     * These lead with a photograph rather than geometry. Two techniques do
     * the heavy lifting, both of which the schema already supports:
     *
     *   Scrim   — a solid rect at partial opacity laid over the photo, so
     *             headline text stays legible whatever the image behind it.
     *   Duotone — the same idea pushed hard (0.75+), which flattens the photo
     *             into the brand colour and is what gives the modern look.
     *
     * The three photo URLs are the ones already proven in the original
     * templates. Unsplash ids are reused deliberately rather than adding new
     * unverified ones — a hero image that 404s ruins the poster, and each is
     * treated differently enough not to read as repetition.
     *
     * Pharmacies should replace these with their own photography via the
     * editor; hotlinked stock is a placeholder, not a dependency to build on.
     * ------------------------------------------------------------------ */

    private const string PhotoPharmacist = "https://images.unsplash.com/photo-1559757175-5700dde675bc?w=1400&q=80&auto=format&fit=crop";
    private const string PhotoClinical = "https://images.unsplash.com/photo-1576091160550-2173dba999ef?w=1400&q=80&auto=format&fit=crop";
    private const string PhotoVaccine = "https://images.unsplash.com/photo-1612277795421-9bc7706a4a34?w=1400&q=80&auto=format&fit=crop";

    private static string VaccinationClinicCanvas() => Canvas(
        name: "Vaccination Clinic Open",
        background: "#0f172a",
        shapes: $$$"""
        [
          {"id":"hero","kind":"image","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "blobKey":"{{{PhotoVaccine}}}","width":1240,"height":920},

          {"id":"scrim","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.62,"draggable":true,"zIndex":2,
           "width":1240,"height":920,"fill":"#0f172a"},

          {"id":"scrim-deep","kind":"rect","position":{"x":0,"y":520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.55,"draggable":true,"zIndex":3,
           "width":1240,"height":400,"fill":"#0f172a"},

          {"id":"tag","kind":"rect","position":{"x":90,"y":140},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":260,"height":62,"fill":"#22d3ee","cornerRadius":31},

          {"id":"tag-text","kind":"text","position":{"x":90,"y":157},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"OPEN NOW","fontSize":26,"fontFamily":"Inter, sans-serif",
           "fill":"#083344","width":260,"align":"center"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":300},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"VACCINATION","fontSize":118,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":430},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"CLINIC","fontSize":118,"fontFamily":"Inter, sans-serif",
           "fill":"#67e8f9","width":1060,"align":"left"},

          {"id":"hero-sub","kind":"text","position":{"x":90,"y":620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Flu, COVID, shingles and travel jabs — all under one roof, most without an appointment.",
           "fontSize":32,"fontFamily":"Inter, sans-serif","fill":"#e2e8f0","width":900,"align":"left"},

          {"id":"panel","kind":"rect","position":{"x":90,"y":840},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "width":1060,"height":560,"fill":"#ffffff","cornerRadius":28},

          {"id":"panel-title","kind":"text","position":{"x":150,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"This week's clinic","fontSize":46,"fontFamily":"Inter, sans-serif",
           "fill":"#0f172a","width":940,"align":"left"},

          {"id":"row-1","kind":"text","position":{"x":150,"y":1000},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Mon – Fri","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":300,"align":"left"},

          {"id":"row-1-val","kind":"text","position":{"x":520,"y":1000},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"9:00am – 6:30pm","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#0f172a","width":560,"align":"left"},

          {"id":"row-1-rule","kind":"line","position":{"x":150,"y":1060},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "points":[0,0,940,0],"stroke":"#e2e8f0","strokeWidth":2},

          {"id":"row-2","kind":"text","position":{"x":150,"y":1090},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Saturday","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":300,"align":"left"},

          {"id":"row-2-val","kind":"text","position":{"x":520,"y":1090},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"9:00am – 1:00pm","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#0f172a","width":560,"align":"left"},

          {"id":"row-2-rule","kind":"line","position":{"x":150,"y":1150},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "points":[0,0,940,0],"stroke":"#e2e8f0","strokeWidth":2},

          {"id":"row-3","kind":"text","position":{"x":150,"y":1180},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Bring","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":300,"align":"left"},

          {"id":"row-3-val","kind":"text","position":{"x":520,"y":1180},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"A short-sleeved top. That's it.","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":560,"align":"left"},

          {"id":"note","kind":"text","position":{"x":150,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Eligible for a free NHS vaccination? We'll check for you at the counter.",
           "fontSize":26,"fontFamily":"Inter, sans-serif","fill":"#64748b","width":940,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":90,"y":1450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "width":1060,"height":160,"fill":"#22d3ee","cornerRadius":28},

          {"id":"cta-title","kind":"text","position":{"x":150,"y":1485},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Walk in today","fontSize":50,"fontFamily":"Inter, sans-serif",
           "fill":"#083344","width":940,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":150,"y":1548},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#155e75","width":940,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1670},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Suitability is assessed before every vaccination. Tell us about allergies and current medicines.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#94a3b8","width":1060,"align":"left"}
        ]
        """);

    private static string AskPharmacistCanvas() => Canvas(
        name: "Ask Your Pharmacist",
        background: "#134e4a",
        shapes: $$$"""
        [
          {"id":"photo","kind":"image","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "blobKey":"{{{PhotoPharmacist}}}","width":1240,"height":1754},

          {"id":"duotone","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.82,"draggable":true,"zIndex":2,
           "width":1240,"height":1754,"fill":"#134e4a"},

          {"id":"accent-wash","kind":"rect","position":{"x":0,"y":1100},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.5,"draggable":true,"zIndex":3,
           "width":1240,"height":654,"fill":"#042f2e"},

          {"id":"rule-top","kind":"line","position":{"x":90,"y":180},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "points":[0,0,1060,0],"stroke":"#5eead4","strokeWidth":4},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":215},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"NO APPOINTMENT  •  NO REFERRAL  •  NO CHARGE","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#5eead4","width":1060,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"ASK YOUR","fontSize":150,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":580},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"PHARMACIST","fontSize":150,"fontFamily":"Inter, sans-serif",
           "fill":"#2dd4bf","width":1060,"align":"left"},

          {"id":"lede","kind":"text","position":{"x":90,"y":800},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Qualified healthcare professionals. On the high street. Available today.",
           "fontSize":38,"fontFamily":"Inter, sans-serif","fill":"#ccfbf1","width":980,"align":"left"},

          {"id":"q-1","kind":"text","position":{"x":90,"y":1180},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"“Is this rash something to worry about?”","fontSize":36,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":1060,"align":"left"},

          {"id":"q-2","kind":"text","position":{"x":90,"y":1260},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"“Can I take these two together?”","fontSize":36,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":1060,"align":"left"},

          {"id":"q-3","kind":"text","position":{"x":90,"y":1340},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"“Do I actually need to see a doctor?”","fontSize":36,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":1060,"align":"left"},

          {"id":"rule-bottom","kind":"line","position":{"x":90,"y":1440},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "points":[0,0,1060,0],"stroke":"#0d9488","strokeWidth":3},

          {"id":"cta","kind":"text","position":{"x":90,"y":1490},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Just walk in and ask.","fontSize":58,"fontFamily":"Inter, sans-serif",
           "fill":"#5eead4","width":1060,"align":"left"},

          {"id":"contact","kind":"text","position":{"x":90,"y":1580},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#99f6e4","width":1060,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1670},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"For anything urgent or severe, contact NHS 111 or emergency services.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#5eead4","width":1060,"align":"left"}
        ]
        """);

    private static string DiabetesScreeningCanvas() => Canvas(
        name: "Diabetes Risk Check",
        background: "#ffffff",
        shapes: $$$"""
        [
          {"id":"photo","kind":"image","position":{"x":620,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "blobKey":"{{{PhotoClinical}}}","width":620,"height":1130},

          {"id":"photo-tint","kind":"rect","position":{"x":620,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.28,"draggable":true,"zIndex":2,
           "width":620,"height":1130,"fill":"#7c2d12"},

          {"id":"left-panel","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":620,"height":1130,"fill":"#fff7ed"},

          {"id":"eyebrow","kind":"text","position":{"x":80,"y":170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"TAKES 10 MINUTES","fontSize":26,"fontFamily":"Inter, sans-serif",
           "fill":"#c2410c","width":460,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":80,"y":240},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"COULD","fontSize":96,"fontFamily":"Inter, sans-serif",
           "fill":"#7c2d12","width":460,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":80,"y":345},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"YOU BE","fontSize":96,"fontFamily":"Inter, sans-serif",
           "fill":"#7c2d12","width":460,"align":"left"},

          {"id":"headline-3","kind":"text","position":{"x":80,"y":450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"AT RISK?","fontSize":96,"fontFamily":"Inter, sans-serif",
           "fill":"#ea580c","width":460,"align":"left"},

          {"id":"rule","kind":"line","position":{"x":80,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "points":[0,0,200,0],"stroke":"#ea580c","strokeWidth":8},

          {"id":"lede","kind":"text","position":{"x":80,"y":650},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Around one in three adults with type 2 diabetes doesn't know they have it.",
           "fontSize":30,"fontFamily":"Inter, sans-serif","fill":"#431407","width":460,"align":"left"},

          {"id":"lede-2","kind":"text","position":{"x":80,"y":830},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"A quick check here can tell you where you stand.","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#9a3412","width":460,"align":"left"},

          {"id":"who-title","kind":"text","position":{"x":90,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Worth checking if you…","fontSize":42,"fontFamily":"Inter, sans-serif",
           "fill":"#431407","width":1060,"align":"left"},

          {"id":"pill-1","kind":"rect","position":{"x":90,"y":1310},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":340,"height":80,"fill":"#ffedd5","cornerRadius":40},

          {"id":"pill-1-text","kind":"text","position":{"x":90,"y":1334},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Are over 40","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#7c2d12","width":340,"align":"center"},

          {"id":"pill-2","kind":"rect","position":{"x":455,"y":1310},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":380,"height":80,"fill":"#ffedd5","cornerRadius":40},

          {"id":"pill-2-text","kind":"text","position":{"x":455,"y":1334},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Have a family history","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#7c2d12","width":380,"align":"center"},

          {"id":"pill-3","kind":"rect","position":{"x":860,"y":1310},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":290,"height":80,"fill":"#ffedd5","cornerRadius":40},

          {"id":"pill-3-text","kind":"text","position":{"x":860,"y":1334},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Feel tired often","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#7c2d12","width":290,"align":"center"},

          {"id":"cta-bar","kind":"rect","position":{"x":0,"y":1490},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1240,"height":180,"fill":"#7c2d12"},

          {"id":"cta-title","kind":"text","position":{"x":90,"y":1530},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Book a risk check","fontSize":52,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":90,"y":1598},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#fdba74","width":1060,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1700},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"A risk check is not a diagnosis. A raised result means we will refer you to your GP for confirmation.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#64748b","width":1060,"align":"left"}
        ]
        """);

    private static string ChildHealthClinicCanvas() => Canvas(
        name: "Child Health Clinic",
        background: "#f0f9ff",
        shapes: $$$"""
        [
          {"id":"blob-1","kind":"circle","position":{"x":80,"y":120},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.5,"draggable":true,"zIndex":1,
           "radius":190,"fill":"#bae6fd"},

          {"id":"blob-2","kind":"circle","position":{"x":1180,"y":1620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.55,"draggable":true,"zIndex":1,
           "radius":230,"fill":"#a5b4fc"},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":150},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"FOR PARENTS AND CARERS","fontSize":26,"fontFamily":"Inter, sans-serif",
           "fill":"#0369a1","width":1060,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":215},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Little ones,","fontSize":106,"fontFamily":"Inter, sans-serif",
           "fill":"#0c4a6e","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":335},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"big worries.","fontSize":106,"fontFamily":"Inter, sans-serif",
           "fill":"#0284c7","width":1060,"align":"left"},

          {"id":"lede","kind":"text","position":{"x":90,"y":500},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"You don't need a GP appointment to get a straight answer about your child.",
           "fontSize":32,"fontFamily":"Inter, sans-serif","fill":"#075985","width":1000,"align":"left"},

          {"id":"photo-frame","kind":"rect","position":{"x":90,"y":640},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":460,"fill":"#0284c7","cornerRadius":32},

          {"id":"photo","kind":"image","position":{"x":110,"y":660},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "blobKey":"{{{PhotoClinical}}}","width":1020,"height":420},

          {"id":"photo-tint","kind":"rect","position":{"x":110,"y":660},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.2,"draggable":true,"zIndex":5,
           "width":1020,"height":420,"fill":"#0c4a6e"},

          {"id":"caption","kind":"rect","position":{"x":110,"y":990},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.85,"draggable":true,"zIndex":6,
           "width":1020,"height":90,"fill":"#0c4a6e"},

          {"id":"caption-text","kind":"text","position":{"x":150,"y":1018},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Free advice, seven days a week","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":940,"align":"left"},

          {"id":"item-1-dot","kind":"circle","position":{"x":120,"y":1200},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "radius":14,"fill":"#0284c7"},

          {"id":"item-1","kind":"text","position":{"x":165,"y":1180},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Coughs, colds and temperatures","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":980,"align":"left"},

          {"id":"item-2-dot","kind":"circle","position":{"x":120,"y":1275},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "radius":14,"fill":"#0284c7"},

          {"id":"item-2","kind":"text","position":{"x":165,"y":1255},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Rashes, eczema and dry skin","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":980,"align":"left"},

          {"id":"item-3-dot","kind":"circle","position":{"x":120,"y":1350},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "radius":14,"fill":"#0284c7"},

          {"id":"item-3","kind":"text","position":{"x":165,"y":1330},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Teething, colic and feeding questions","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":980,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":90,"y":1450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "width":1060,"height":160,"fill":"#0c4a6e","cornerRadius":32},

          {"id":"cta-title","kind":"text","position":{"x":150,"y":1485},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Come in and ask","fontSize":50,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":940,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":150,"y":1548},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#7dd3fc","width":940,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1665},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"If your child is very unwell, drowsy, or hard to wake, contact NHS 111 or emergency services straight away.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#0369a1","width":1060,"align":"left"}
        ]
        """);

    private static string MensHealthCanvas() => Canvas(
        name: "Men's Health MOT",
        background: "#0a0a0a",
        shapes: $$$"""
        [
          {"id":"eyebrow","kind":"text","position":{"x":90,"y":170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"15 MINUTES  •  NO APPOINTMENT","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#a3e635","width":1060,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":250},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"THE","fontSize":150,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":400},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"MEN'S","fontSize":150,"fontFamily":"Inter, sans-serif",
           "fill":"#a3e635","width":1060,"align":"left"},

          {"id":"headline-3","kind":"text","position":{"x":90,"y":550},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"MOT","fontSize":150,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"lede","kind":"text","position":{"x":90,"y":760},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"You service the car every year. When did you last check yourself?",
           "fontSize":36,"fontFamily":"Inter, sans-serif","fill":"#d4d4d8","width":1000,"align":"left"},

          {"id":"stat-rule","kind":"line","position":{"x":90,"y":890},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "points":[0,0,1060,0],"stroke":"#3f3f46","strokeWidth":2},

          {"id":"check-1","kind":"text","position":{"x":90,"y":930},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"01","fontSize":30,"fontFamily":"Inter, sans-serif",
           "fill":"#a3e635","width":120,"align":"left"},

          {"id":"check-1-text","kind":"text","position":{"x":220,"y":924},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Blood pressure","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":900,"align":"left"},

          {"id":"check-2","kind":"text","position":{"x":90,"y":1010},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"02","fontSize":30,"fontFamily":"Inter, sans-serif",
           "fill":"#a3e635","width":120,"align":"left"},

          {"id":"check-2-text","kind":"text","position":{"x":220,"y":1004},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Cholesterol","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":900,"align":"left"},

          {"id":"check-3","kind":"text","position":{"x":90,"y":1090},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"03","fontSize":30,"fontFamily":"Inter, sans-serif",
           "fill":"#a3e635","width":120,"align":"left"},

          {"id":"check-3-text","kind":"text","position":{"x":220,"y":1084},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Weight and BMI","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":900,"align":"left"},

          {"id":"check-4","kind":"text","position":{"x":90,"y":1170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"04","fontSize":30,"fontFamily":"Inter, sans-serif",
           "fill":"#a3e635","width":120,"align":"left"},

          {"id":"check-4-text","kind":"text","position":{"x":220,"y":1164},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"A frank conversation","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":900,"align":"left"},

          {"id":"photo","kind":"image","position":{"x":0,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "blobKey":"{{{PhotoPharmacist}}}","width":1240,"height":464},

          {"id":"photo-scrim","kind":"rect","position":{"x":0,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.72,"draggable":true,"zIndex":3,
           "width":1240,"height":464,"fill":"#0a0a0a"},

          {"id":"cta-bar","kind":"rect","position":{"x":90,"y":1380},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1060,"height":170,"fill":"#a3e635","cornerRadius":16},

          {"id":"cta-title","kind":"text","position":{"x":140,"y":1418},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Book yours today","fontSize":52,"fontFamily":"Inter, sans-serif",
           "fill":"#1a2e05","width":960,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":140,"y":1484},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#365314","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Screening checks are not diagnostic. Your pharmacist will explain your results and refer you where needed.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#a1a1aa","width":1060,"align":"left"}
        ]
        """);

    private static string AsthmaReviewCanvas() => Canvas(
        name: "Inhaler Technique Review",
        background: "#faf5ff",
        shapes: $$$"""
        [
          {"id":"header","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":680,"fill":"#4c1d95"},

          {"id":"photo","kind":"image","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "blobKey":"{{{PhotoVaccine}}}","width":1240,"height":680},

          {"id":"photo-duotone","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.78,"draggable":true,"zIndex":3,
           "width":1240,"height":680,"fill":"#4c1d95"},

          {"id":"wedge","kind":"rect","position":{"x":-120,"y":600},"rotation":-5,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1520,"height":220,"fill":"#faf5ff"},

          {"id":"stat","kind":"text","position":{"x":90,"y":170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"UP TO 9 IN 10","fontSize":34,"fontFamily":"Inter, sans-serif",
           "fill":"#c4b5fd","width":1060,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":240},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"people use","fontSize":102,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":355},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"their inhaler","fontSize":102,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-3","kind":"text","position":{"x":90,"y":470},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"wrong.","fontSize":102,"fontFamily":"Inter, sans-serif",
           "fill":"#a78bfa","width":1060,"align":"left"},

          {"id":"lede","kind":"text","position":{"x":90,"y":760},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"If the medicine isn't reaching your lungs, it isn't working. We'll watch your technique and put it right — free, in a few minutes.",
           "fontSize":32,"fontFamily":"Inter, sans-serif","fill":"#3b0764","width":1060,"align":"left"},

          {"id":"card-1","kind":"rect","position":{"x":90,"y":990},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1060,"height":130,"fill":"#ffffff","cornerRadius":22},

          {"id":"card-1-num","kind":"text","position":{"x":140,"y":1025},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"01","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#a78bfa","width":100,"align":"left"},

          {"id":"card-1-text","kind":"text","position":{"x":260,"y":1032},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Bring the inhalers you actually use","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#3b0764","width":840,"align":"left"},

          {"id":"card-2","kind":"rect","position":{"x":90,"y":1140},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1060,"height":130,"fill":"#ffffff","cornerRadius":22},

          {"id":"card-2-num","kind":"text","position":{"x":140,"y":1175},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"02","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#a78bfa","width":100,"align":"left"},

          {"id":"card-2-text","kind":"text","position":{"x":260,"y":1182},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Show us how you take them","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#3b0764","width":840,"align":"left"},

          {"id":"card-3","kind":"rect","position":{"x":90,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1060,"height":130,"fill":"#ffffff","cornerRadius":22},

          {"id":"card-3-num","kind":"text","position":{"x":140,"y":1325},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"03","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#a78bfa","width":100,"align":"left"},

          {"id":"card-3-text","kind":"text","position":{"x":260,"y":1332},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Leave breathing easier","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#3b0764","width":840,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":0,"y":1500},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "width":1240,"height":170,"fill":"#4c1d95"},

          {"id":"cta-title","kind":"text","position":{"x":90,"y":1535},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Ask for an inhaler check","fontSize":50,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":90,"y":1600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#c4b5fd","width":1060,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1700},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"Never stop or change prescribed inhalers without advice. If your breathing worsens suddenly, seek urgent medical help.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#7c3aed","width":1060,"align":"left"}
        ]
        """);

    private static string BloodPressureCanvas() => Canvas(
        name: "Blood Pressure Check",
        background: "#f8fafc",
        shapes: """
        [
          {"id":"band-accent","kind":"rect","position":{"x":-120,"y":300},"rotation":-8,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1520,"height":16,"fill":"#22d3ee"},

          {"id":"band","kind":"rect","position":{"x":-120,"y":330},"rotation":-8,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "width":1520,"height":430,"fill":"#0e7490"},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":150},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"FREE IN-STORE SERVICE","fontSize":30,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":1060,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":390},"rotation":-8,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"KNOW YOUR","fontSize":124,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":530},"rotation":-8,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"NUMBERS","fontSize":124,"fontFamily":"Inter, sans-serif",
           "fill":"#a5f3fc","width":1060,"align":"left"},

          {"id":"stat-ring","kind":"circle","position":{"x":940,"y":1010},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "radius":210,"fill":"#ecfeff","stroke":"#0e7490","strokeWidth":10},

          {"id":"stat-value","kind":"text","position":{"x":740,"y":940},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"120/80","fontSize":86,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":400,"align":"center"},

          {"id":"stat-label","kind":"text","position":{"x":740,"y":1045},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"the healthy target","fontSize":26,"fontFamily":"Inter, sans-serif",
           "fill":"#155e75","width":400,"align":"center"},

          {"id":"lede","kind":"text","position":{"x":90,"y":860},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"High blood pressure has no symptoms.","fontSize":44,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":600,"align":"left"},

          {"id":"lede-2","kind":"text","position":{"x":90,"y":950},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"A two-minute check today could protect your heart for years.",
           "fontSize":28,"fontFamily":"Inter, sans-serif","fill":"#475569","width":600,"align":"left"},

          {"id":"chip-1","kind":"rect","position":{"x":90,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":330,"height":74,"fill":"#ecfeff","cornerRadius":37},

          {"id":"chip-1-text","kind":"text","position":{"x":90,"y":1312},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"No appointment","fontSize":26,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":330,"align":"center"},

          {"id":"chip-2","kind":"rect","position":{"x":445,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":300,"height":74,"fill":"#ecfeff","cornerRadius":37},

          {"id":"chip-2-text","kind":"text","position":{"x":445,"y":1312},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Takes 2 minutes","fontSize":26,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":300,"align":"center"},

          {"id":"chip-3","kind":"rect","position":{"x":770,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":380,"height":74,"fill":"#ecfeff","cornerRadius":37},

          {"id":"chip-3-text","kind":"text","position":{"x":770,"y":1312},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Results explained","fontSize":26,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":380,"align":"center"},

          {"id":"cta-bar","kind":"rect","position":{"x":0,"y":1500},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1240,"height":180,"fill":"#0f172a"},

          {"id":"cta-title","kind":"text","position":{"x":90,"y":1540},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Ask at the counter","fontSize":52,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":700,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":90,"y":1608},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#94a3b8","width":700,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1706},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"A reading is not a diagnosis. Speak to your pharmacist or GP about your result.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#64748b","width":1060,"align":"left"}
        ]
        """);

    private static string WinterWellnessCanvas() => Canvas(
        name: "Winter Wellness Offer",
        background: "#0c4a6e",
        shapes: """
        [
          {"id":"glow-1","kind":"circle","position":{"x":1120,"y":260},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.25,"draggable":true,"zIndex":1,
           "radius":320,"fill":"#38bdf8"},

          {"id":"glow-2","kind":"circle","position":{"x":120,"y":1500},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.18,"draggable":true,"zIndex":1,
           "radius":260,"fill":"#22d3ee"},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"WINTER 2026","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#7dd3fc","width":1060,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":250},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"STAY WELL","fontSize":140,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":400},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"THIS WINTER","fontSize":140,"fontFamily":"Inter, sans-serif",
           "fill":"#38bdf8","width":1060,"align":"left"},

          {"id":"rule","kind":"line","position":{"x":90,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "points":[0,0,300,0],"stroke":"#22d3ee","strokeWidth":8},

          {"id":"sub","kind":"text","position":{"x":90,"y":650},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Everything you need to get through the cold months, in one place.",
           "fontSize":34,"fontFamily":"Inter, sans-serif","fill":"#bae6fd","width":760,"align":"left"},

          {"id":"badge","kind":"circle","position":{"x":1010,"y":690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":150,"fill":"#facc15"},

          {"id":"badge-value","kind":"text","position":{"x":860,"y":625},"rotation":-10,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"20% OFF","fontSize":52,"fontFamily":"Inter, sans-serif",
           "fill":"#422006","width":300,"align":"center"},

          {"id":"badge-sub","kind":"text","position":{"x":860,"y":700},"rotation":-10,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"selected lines","fontSize":22,"fontFamily":"Inter, sans-serif",
           "fill":"#713f12","width":300,"align":"center"},

          {"id":"card-1","kind":"rect","position":{"x":90,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":220,"fill":"#075985","cornerRadius":24},

          {"id":"card-1-title","kind":"text","position":{"x":130,"y":950},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Flu jabs","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":420,"align":"left"},

          {"id":"card-1-body","kind":"text","position":{"x":130,"y":1015},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Walk in any weekday, no booking needed.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#bae6fd","width":420,"align":"left"},

          {"id":"card-2","kind":"rect","position":{"x":650,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":220,"fill":"#075985","cornerRadius":24},

          {"id":"card-2-title","kind":"text","position":{"x":690,"y":950},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Vitamin D","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":420,"align":"left"},

          {"id":"card-2-body","kind":"text","position":{"x":690,"y":1015},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Recommended for everyone October to March.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#bae6fd","width":420,"align":"left"},

          {"id":"card-3","kind":"rect","position":{"x":90,"y":1160},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":220,"fill":"#075985","cornerRadius":24},

          {"id":"card-3-title","kind":"text","position":{"x":130,"y":1210},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Cold + flu","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":420,"align":"left"},

          {"id":"card-3-body","kind":"text","position":{"x":130,"y":1275},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Pharmacist-picked remedies for the whole family.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#bae6fd","width":420,"align":"left"},

          {"id":"card-4","kind":"rect","position":{"x":650,"y":1160},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":220,"fill":"#075985","cornerRadius":24},

          {"id":"card-4-title","kind":"text","position":{"x":690,"y":1210},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Free advice","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":420,"align":"left"},

          {"id":"card-4-body","kind":"text","position":{"x":690,"y":1275},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Speak to a pharmacist, no appointment.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#bae6fd","width":420,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":90,"y":1450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1060,"height":170,"fill":"#facc15","cornerRadius":24},

          {"id":"cta-title","kind":"text","position":{"x":130,"y":1490},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Pop in this week","fontSize":52,"fontFamily":"Inter, sans-serif",
           "fill":"#422006","width":980,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":130,"y":1555},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#713f12","width":980,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1680},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Offer subject to availability. Ask a pharmacist whether a product is suitable for you.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#7dd3fc","width":1060,"align":"left"}
        ]
        """);

    private static string PharmacyFirstCanvas() => Canvas(
        name: "Pharmacy First Services",
        background: "#ffffff",
        shapes: """
        [
          {"id":"header","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":520,"fill":"#065f46"},

          {"id":"header-wedge","kind":"rect","position":{"x":-120,"y":420},"rotation":-6,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "width":1520,"height":200,"fill":"#ffffff"},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":120},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"SEEN TODAY, NOT NEXT WEEK","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#6ee7b7","width":1060,"align":"left"},

          {"id":"headline","kind":"text","position":{"x":90,"y":190},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"START WITH","fontSize":110,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":305},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"{{pharmacy_name}}","fontSize":110,"fontFamily":"Inter, sans-serif",
           "fill":"#34d399","width":1060,"align":"left"},

          {"id":"lede","kind":"text","position":{"x":90,"y":640},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Your pharmacist can assess and treat these conditions without a GP appointment.",
           "fontSize":32,"fontFamily":"Inter, sans-serif","fill":"#334155","width":1060,"align":"left"},

          {"id":"row-1","kind":"rect","position":{"x":90,"y":770},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":96,"fill":"#f0fdf4","cornerRadius":18},

          {"id":"row-1-dot","kind":"circle","position":{"x":150,"y":818},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":16,"fill":"#059669"},

          {"id":"row-1-text","kind":"text","position":{"x":200,"y":798},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Sore throat","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#064e3b","width":900,"align":"left"},

          {"id":"row-2","kind":"rect","position":{"x":90,"y":886},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":96,"fill":"#f0fdf4","cornerRadius":18},

          {"id":"row-2-dot","kind":"circle","position":{"x":150,"y":934},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":16,"fill":"#059669"},

          {"id":"row-2-text","kind":"text","position":{"x":200,"y":914},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Earache (ages 1–17)","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#064e3b","width":900,"align":"left"},

          {"id":"row-3","kind":"rect","position":{"x":90,"y":1002},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":96,"fill":"#f0fdf4","cornerRadius":18},

          {"id":"row-3-dot","kind":"circle","position":{"x":150,"y":1050},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":16,"fill":"#059669"},

          {"id":"row-3-text","kind":"text","position":{"x":200,"y":1030},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Sinusitis","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#064e3b","width":900,"align":"left"},

          {"id":"row-4","kind":"rect","position":{"x":90,"y":1118},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":96,"fill":"#f0fdf4","cornerRadius":18},

          {"id":"row-4-dot","kind":"circle","position":{"x":150,"y":1166},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":16,"fill":"#059669"},

          {"id":"row-4-text","kind":"text","position":{"x":200,"y":1146},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Impetigo","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#064e3b","width":900,"align":"left"},

          {"id":"row-5","kind":"rect","position":{"x":90,"y":1234},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":96,"fill":"#f0fdf4","cornerRadius":18},

          {"id":"row-5-dot","kind":"circle","position":{"x":150,"y":1282},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":16,"fill":"#059669"},

          {"id":"row-5-text","kind":"text","position":{"x":200,"y":1262},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Shingles","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#064e3b","width":900,"align":"left"},

          {"id":"row-6","kind":"rect","position":{"x":90,"y":1350},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":96,"fill":"#f0fdf4","cornerRadius":18},

          {"id":"row-6-dot","kind":"circle","position":{"x":150,"y":1398},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":16,"fill":"#059669"},

          {"id":"row-6-text","kind":"text","position":{"x":200,"y":1378},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Urinary tract infection (women 16–64)","fontSize":38,
           "fontFamily":"Inter, sans-serif","fill":"#064e3b","width":900,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":0,"y":1520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1240,"height":160,"fill":"#065f46"},

          {"id":"cta-title","kind":"text","position":{"x":90,"y":1555},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Just ask — no appointment","fontSize":46,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1060,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":90,"y":1615},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#6ee7b7","width":1060,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1706},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Eligibility criteria apply and vary by condition. Your pharmacist will advise if you need to see a GP.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#64748b","width":1060,"align":"left"}
        ]
        """);

    private static string MenopauseSupportCanvas() => Canvas(
        name: "Menopause Support",
        background: "#fdf2f8",
        shapes: """
        [
          {"id":"arc","kind":"circle","position":{"x":1240,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "radius":460,"fill":"#fbcfe8"},

          {"id":"arc-2","kind":"circle","position":{"x":0,"y":1754},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.7,"draggable":true,"zIndex":1,
           "radius":360,"fill":"#f9a8d4"},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":220},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"YOU DON'T HAVE TO PUSH THROUGH IT","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#9d174d","width":760,"align":"left"},

          {"id":"headline","kind":"text","position":{"x":90,"y":290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Menopause","fontSize":118,"fontFamily":"Inter, sans-serif",
           "fill":"#500724","width":900,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"support, here.","fontSize":118,"fontFamily":"Inter, sans-serif",
           "fill":"#be185d","width":900,"align":"left"},

          {"id":"rule","kind":"line","position":{"x":90,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "points":[0,0,240,0],"stroke":"#be185d","strokeWidth":6},

          {"id":"lede","kind":"text","position":{"x":90,"y":650},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Hot flushes, broken sleep, low mood, brain fog. These are treatable — and worth talking about.",
           "fontSize":34,"fontFamily":"Inter, sans-serif","fill":"#831843","width":900,"align":"left"},

          {"id":"panel","kind":"rect","position":{"x":90,"y":840},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1060,"height":520,"fill":"#ffffff","cornerRadius":32},

          {"id":"panel-title","kind":"text","position":{"x":150,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"What we can help with","fontSize":44,"fontFamily":"Inter, sans-serif",
           "fill":"#500724","width":940,"align":"left"},

          {"id":"item-1","kind":"text","position":{"x":150,"y":1000},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"—   A private conversation, no appointment needed","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#831843","width":940,"align":"left"},

          {"id":"item-2","kind":"text","position":{"x":150,"y":1075},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"—   Understanding your HRT options","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#831843","width":940,"align":"left"},

          {"id":"item-3","kind":"text","position":{"x":150,"y":1150},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"—   Managing symptoms day to day","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#831843","width":940,"align":"left"},

          {"id":"item-4","kind":"text","position":{"x":150,"y":1225},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"—   Bone and heart health as you age","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#831843","width":940,"align":"left"},

          {"id":"quote","kind":"text","position":{"x":150,"y":1300},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Ask for a quiet word — we have a private consultation room.",
           "fontSize":26,"fontFamily":"Inter, sans-serif","fill":"#be185d","width":940,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":90,"y":1430},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1060,"height":160,"fill":"#500724","cornerRadius":32},

          {"id":"cta-title","kind":"text","position":{"x":150,"y":1465},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Come and talk to us","fontSize":48,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":940,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":150,"y":1527},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#f9a8d4","width":940,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1660},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Information only and not a substitute for medical advice. Your pharmacist will refer you to your GP where appropriate.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#9d174d","width":1060,"align":"left"}
        ]
        """);

    private static string MedicineDeliveryCanvas() => Canvas(
        name: "Free Prescription Delivery",
        background: "#1e1b4b",
        shapes: """
        [
          {"id":"stripe-1","kind":"rect","position":{"x":760,"y":-200},"rotation":22,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":260,"height":2200,"fill":"#312e81"},

          {"id":"stripe-2","kind":"rect","position":{"x":1010,"y":-200},"rotation":22,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":120,"height":2200,"fill":"#4338ca"},

          {"id":"stripe-3","kind":"rect","position":{"x":1150,"y":-200},"rotation":22,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":48,"height":2200,"fill":"#818cf8"},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":200},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"NO CHARGE  •  NO FUSS","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#a5b4fc","width":700,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":280},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"WE BRING","fontSize":132,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":700,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"IT TO YOU","fontSize":132,"fontFamily":"Inter, sans-serif",
           "fill":"#818cf8","width":700,"align":"left"},

          {"id":"lede","kind":"text","position":{"x":90,"y":620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Free local delivery on repeat prescriptions, straight to your door.",
           "fontSize":34,"fontFamily":"Inter, sans-serif","fill":"#c7d2fe","width":640,"align":"left"},

          {"id":"step-1-num","kind":"circle","position":{"x":150,"y":870},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "radius":52,"fill":"#818cf8"},

          {"id":"step-1-digit","kind":"text","position":{"x":100,"y":838},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"1","fontSize":54,"fontFamily":"Inter, sans-serif",
           "fill":"#1e1b4b","width":100,"align":"center"},

          {"id":"step-1-title","kind":"text","position":{"x":240,"y":830},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Tell us once","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":700,"align":"left"},

          {"id":"step-1-body","kind":"text","position":{"x":240,"y":885},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Sign up in store or over the phone.","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#a5b4fc","width":620,"align":"left"},

          {"id":"step-2-num","kind":"circle","position":{"x":150,"y":1050},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "radius":52,"fill":"#818cf8"},

          {"id":"step-2-digit","kind":"text","position":{"x":100,"y":1018},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"2","fontSize":54,"fontFamily":"Inter, sans-serif",
           "fill":"#1e1b4b","width":100,"align":"center"},

          {"id":"step-2-title","kind":"text","position":{"x":240,"y":1010},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"We handle the rest","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":700,"align":"left"},

          {"id":"step-2-body","kind":"text","position":{"x":240,"y":1065},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"We order from your surgery each month.","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#a5b4fc","width":620,"align":"left"},

          {"id":"step-3-num","kind":"circle","position":{"x":150,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "radius":52,"fill":"#818cf8"},

          {"id":"step-3-digit","kind":"text","position":{"x":100,"y":1198},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"3","fontSize":54,"fontFamily":"Inter, sans-serif",
           "fill":"#1e1b4b","width":100,"align":"center"},

          {"id":"step-3-title","kind":"text","position":{"x":240,"y":1190},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"It arrives","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":700,"align":"left"},

          {"id":"step-3-body","kind":"text","position":{"x":240,"y":1245},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Delivered free, on a day that suits you.","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#a5b4fc","width":620,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":90,"y":1400},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1060,"height":180,"fill":"#818cf8","cornerRadius":28},

          {"id":"cta-title","kind":"text","position":{"x":140,"y":1440},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Sign up in 60 seconds","fontSize":52,"fontFamily":"Inter, sans-serif",
           "fill":"#1e1b4b","width":960,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":140,"y":1508},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#312e81","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1650},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Free delivery within our local area. Ask in store for the postcodes we cover and typical delivery days.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#a5b4fc","width":1060,"align":"left"}
        ]
        """);

    private static string NewYearHealthCanvas() => Canvas(
        name: "New Year Health Check",
        background: "#f1f5f9",
        shapes: """
        [
          {"id":"panel-left","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":740,"fill":"#111827"},

          {"id":"accent-block","kind":"rect","position":{"x":840,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "width":400,"height":740,"fill":"#f97316"},

          {"id":"accent-cut","kind":"rect","position":{"x":760,"y":-120},"rotation":14,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":300,"height":1000,"fill":"#111827"},

          {"id":"year","kind":"text","position":{"x":880,"y":250},"rotation":90,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"2026","fontSize":150,"fontFamily":"Inter, sans-serif",
           "fill":"#fdba74","width":600,"align":"left"},

          {"id":"eyebrow","kind":"text","position":{"x":90,"y":170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"A BETTER START","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#f97316","width":700,"align":"left"},

          {"id":"headline-1","kind":"text","position":{"x":90,"y":250},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"NEW YEAR","fontSize":126,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":740,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":90,"y":390},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"HEALTH CHECK","fontSize":92,"fontFamily":"Inter, sans-serif",
           "fill":"#fb923c","width":740,"align":"left"},

          {"id":"sub","kind":"text","position":{"x":90,"y":530},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Fifteen minutes. Four measurements. One honest conversation.",
           "fontSize":30,"fontFamily":"Inter, sans-serif","fill":"#cbd5e1","width":700,"align":"left"},

          {"id":"metric-1","kind":"rect","position":{"x":90,"y":830},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":190,"fill":"#ffffff","cornerRadius":20},

          {"id":"metric-1-title","kind":"text","position":{"x":130,"y":870},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Blood pressure","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#111827","width":420,"align":"left"},

          {"id":"metric-1-body","kind":"text","position":{"x":130,"y":925},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"The silent one. Worth knowing.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#64748b","width":420,"align":"left"},

          {"id":"metric-2","kind":"rect","position":{"x":650,"y":830},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":190,"fill":"#ffffff","cornerRadius":20},

          {"id":"metric-2-title","kind":"text","position":{"x":690,"y":870},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Cholesterol","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#111827","width":420,"align":"left"},

          {"id":"metric-2-body","kind":"text","position":{"x":690,"y":925},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"A finger-prick test, results while you wait.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#64748b","width":420,"align":"left"},

          {"id":"metric-3","kind":"rect","position":{"x":90,"y":1060},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":190,"fill":"#ffffff","cornerRadius":20},

          {"id":"metric-3-title","kind":"text","position":{"x":130,"y":1100},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Weight + BMI","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#111827","width":420,"align":"left"},

          {"id":"metric-3-body","kind":"text","position":{"x":130,"y":1155},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"No judgement, just a starting point.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#64748b","width":420,"align":"left"},

          {"id":"metric-4","kind":"rect","position":{"x":650,"y":1060},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":500,"height":190,"fill":"#ffffff","cornerRadius":20},

          {"id":"metric-4-title","kind":"text","position":{"x":690,"y":1100},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Diabetes risk","fontSize":38,"fontFamily":"Inter, sans-serif",
           "fill":"#111827","width":420,"align":"left"},

          {"id":"metric-4-body","kind":"text","position":{"x":690,"y":1155},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"A few questions and a quick check.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#64748b","width":420,"align":"left"},

          {"id":"price-rule","kind":"line","position":{"x":90,"y":1330},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "points":[0,0,1060,0],"stroke":"#cbd5e1","strokeWidth":3},

          {"id":"price","kind":"text","position":{"x":90,"y":1370},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Book yours — ask at the counter","fontSize":46,
           "fontFamily":"Inter, sans-serif","fill":"#111827","width":1060,"align":"left"},

          {"id":"cta-bar","kind":"rect","position":{"x":0,"y":1500},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":1240,"height":170,"fill":"#f97316"},

          {"id":"cta-title","kind":"text","position":{"x":90,"y":1535},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Start the year knowing","fontSize":50,"fontFamily":"Inter, sans-serif",
           "fill":"#431407","width":1060,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":90,"y":1600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{pharmacy_name}}  •  {{phone}}  •  {{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":1060,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":90,"y":1700},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Screening checks are not diagnostic. Your pharmacist will explain your results and refer you where needed.",
           "fontSize":17,"fontFamily":"Inter, sans-serif","fill":"#64748b","width":1060,"align":"left"}
        ]
        """);

    private static string FluVaccinationCanvas() => Canvas(
        name: "Flu Vaccination Reminder",
        background: "#ecfeff",
        shapes: """
        [
          {"id":"top-bar","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":60,"fill":"#0e7490"},

          {"id":"hero-circle","kind":"circle","position":{"x":1080,"y":260},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.18,"draggable":true,"zIndex":2,
           "radius":220,"fill":"#06b6d4"},

          {"id":"hero-img","kind":"image","position":{"x":320,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "blobKey":"https://images.unsplash.com/photo-1612277795421-9bc7706a4a34?w=900&q=80&auto=format&fit=crop",
           "width":600,"height":400},

          {"id":"headline-1","kind":"text","position":{"x":80,"y":200},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"FLU SHOTS","fontSize":110,"fontFamily":"Inter, sans-serif",
           "fill":"#164e63","width":1080,"align":"left"},

          {"id":"headline-2","kind":"text","position":{"x":80,"y":340},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"AVAILABLE NOW","fontSize":110,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":1080,"align":"left"},

          {"id":"subtitle","kind":"text","position":{"x":80,"y":500},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Walk-ins welcome • Most insurance accepted","fontSize":36,
           "fontFamily":"Inter, sans-serif","fill":"#155e75","width":1080,"align":"left"},

          {"id":"divider","kind":"line","position":{"x":80,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "points":[0,0,1080,0],"stroke":"#06b6d4","strokeWidth":4},

          {"id":"bullet-1","kind":"text","position":{"x":80,"y":660},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Quick 15-minute appointment","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a"},

          {"id":"bullet-2","kind":"text","position":{"x":80,"y":730},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Available daily 9am – 7pm","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a"},

          {"id":"bullet-3","kind":"text","position":{"x":80,"y":800},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Ask about high-dose options","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a"},

          {"id":"cta-box","kind":"rect","position":{"x":80,"y":1380},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1080,"height":200,"fill":"#0e7490","cornerRadius":20},

          {"id":"cta-title","kind":"text","position":{"x":140,"y":1420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Visit our pharmacy today","fontSize":56,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":960,"align":"left"},

          {"id":"cta-sub","kind":"text","position":{"x":140,"y":1505},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"{{phone}}  •  {{address}}","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#cffafe","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1670},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Talk to your pharmacist about side effects and contraindications. Service subject to availability.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string VitaminDCanvas() => Canvas(
        name: "Vitamin D Awareness",
        background: "#fef3c7",
        shapes: """
        [
          {"id":"sun-outer","kind":"circle","position":{"x":1080,"y":240},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.55,"draggable":true,"zIndex":1,
           "radius":260,"fill":"#fbbf24"},

          {"id":"sun-inner","kind":"circle","position":{"x":1080,"y":240},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "radius":150,"fill":"#f59e0b"},

          {"id":"headline","kind":"text","position":{"x":80,"y":260},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Are You Vitamin D Deficient?","fontSize":76,
           "fontFamily":"Inter, sans-serif","fill":"#92400e","width":960,"align":"left"},

          {"id":"sub","kind":"text","position":{"x":80,"y":520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Common signs to watch for:","fontSize":36,
           "fontFamily":"Inter, sans-serif","fill":"#78350f"},

          {"id":"sym-1","kind":"text","position":{"x":80,"y":620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  Persistent fatigue and low mood","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#1f2937"},
          {"id":"sym-2","kind":"text","position":{"x":80,"y":690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  Frequent muscle or bone aches","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#1f2937"},
          {"id":"sym-3","kind":"text","position":{"x":80,"y":760},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  Slow-healing wounds","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#1f2937"},
          {"id":"sym-4","kind":"text","position":{"x":80,"y":830},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  Reduced immune resilience","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#1f2937"},

          {"id":"hero-img","kind":"image","position":{"x":320,"y":940},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "blobKey":"https://images.unsplash.com/photo-1559757175-5700dde675bc?w=900&q=80&auto=format&fit=crop",
           "width":600,"height":300},

          {"id":"box","kind":"rect","position":{"x":80,"y":1280},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1080,"height":280,"fill":"#fffbeb","stroke":"#f59e0b","strokeWidth":4,
           "cornerRadius":16},

          {"id":"box-title","kind":"text","position":{"x":140,"y":1320},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Ask the pharmacist","fontSize":56,"fontFamily":"Inter, sans-serif",
           "fill":"#92400e","width":960,"align":"left"},
          {"id":"box-sub","kind":"text","position":{"x":140,"y":1410},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Free 5-minute consultation. We can recommend a test or supplement.",
           "fontSize":26,"fontFamily":"Inter, sans-serif","fill":"#78350f","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1680},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"This information is for educational purposes only. Consult a licensed pharmacist or physician before starting any supplement.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#57534e","width":1080,"align":"left"}
        ]
        """);

    private static string LoyaltyRewardsCanvas() => Canvas(
        name: "Loyalty Rewards Promo",
        background: "#1e1b4b",
        shapes: """
        [
          {"id":"top-bar","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":80,"fill":"#6366f1"},

          {"id":"glow","kind":"circle","position":{"x":1100,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.25,"draggable":true,"zIndex":1,
           "radius":340,"fill":"#a78bfa"},

          {"id":"head-1","kind":"text","position":{"x":80,"y":180},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"Earn Points.","fontSize":110,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1080,"align":"left"},
          {"id":"head-2","kind":"text","position":{"x":80,"y":320},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"Save More.","fontSize":110,"fontFamily":"Inter, sans-serif",
           "fill":"#fbbf24","width":1080,"align":"left"},

          {"id":"big-pct","kind":"text","position":{"x":80,"y":540},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"10%","fontSize":280,"fontFamily":"Inter, sans-serif",
           "fill":"#fbbf24","width":600,"align":"left"},

          {"id":"big-pct-sub","kind":"text","position":{"x":80,"y":880},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"BACK ON EVERY PURCHASE","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#c7d2fe","width":1080,"align":"left"},

          {"id":"steps-h","kind":"text","position":{"x":80,"y":1080},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"How it works","fontSize":40,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff"},

          {"id":"step-1-c","kind":"circle","position":{"x":130,"y":1200},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#6366f1"},
          {"id":"step-1-n","kind":"text","position":{"x":117,"y":1175},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"1","fontSize":42,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"step-1-t","kind":"text","position":{"x":200,"y":1185},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Sign up at any register","fontSize":28,"fontFamily":"Inter, sans-serif","fill":"#e0e7ff"},

          {"id":"step-2-c","kind":"circle","position":{"x":130,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#6366f1"},
          {"id":"step-2-n","kind":"text","position":{"x":117,"y":1265},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"2","fontSize":42,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"step-2-t","kind":"text","position":{"x":200,"y":1275},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Earn 1 point per dollar","fontSize":28,"fontFamily":"Inter, sans-serif","fill":"#e0e7ff"},

          {"id":"step-3-c","kind":"circle","position":{"x":130,"y":1380},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#6366f1"},
          {"id":"step-3-n","kind":"text","position":{"x":117,"y":1355},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"3","fontSize":42,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"step-3-t","kind":"text","position":{"x":200,"y":1365},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Redeem rewards instantly","fontSize":28,"fontFamily":"Inter, sans-serif","fill":"#e0e7ff"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Limited-time offer. Points have no cash value. See store for full terms.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#94a3b8","width":1080,"align":"left"}
        ]
        """);

    private static string HandHygieneCanvas() => Canvas(
        name: "Hand Hygiene Steps",
        background: "#faf5ff",
        shapes: """
        [
          {"id":"hdr-bar","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":140,"fill":"#6b21a8"},

          {"id":"hdr-txt","kind":"text","position":{"x":80,"y":40},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"WASH YOUR HANDS","fontSize":64,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":1080,"align":"left"},

          {"id":"sub","kind":"text","position":{"x":80,"y":220},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"20 seconds saves lives","fontSize":56,
           "fontFamily":"Inter, sans-serif","fill":"#6b21a8","width":1080,"align":"left"},

          {"id":"card-1","kind":"rect","position":{"x":80,"y":380},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":140,"fill":"#ffffff","stroke":"#c084fc","strokeWidth":2,"cornerRadius":12},
          {"id":"c1-num","kind":"circle","position":{"x":160,"y":450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#a855f7"},
          {"id":"c1-numt","kind":"text","position":{"x":146,"y":425},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"1","fontSize":42,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"c1-t1","kind":"text","position":{"x":230,"y":410},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Wet hands with clean water","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#581c87","width":900,"align":"left"},
          {"id":"c1-t2","kind":"text","position":{"x":230,"y":460},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Cool or warm — it doesn't matter.","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#6b21a8","width":900,"align":"left"},

          {"id":"card-2","kind":"rect","position":{"x":80,"y":560},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":140,"fill":"#ffffff","stroke":"#c084fc","strokeWidth":2,"cornerRadius":12},
          {"id":"c2-num","kind":"circle","position":{"x":160,"y":630},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#a855f7"},
          {"id":"c2-numt","kind":"text","position":{"x":146,"y":605},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"2","fontSize":42,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"c2-t1","kind":"text","position":{"x":230,"y":590},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Lather every surface","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#581c87","width":900,"align":"left"},
          {"id":"c2-t2","kind":"text","position":{"x":230,"y":640},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Backs of hands, between fingers, under nails.","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#6b21a8","width":900,"align":"left"},

          {"id":"card-3","kind":"rect","position":{"x":80,"y":740},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":140,"fill":"#ffffff","stroke":"#c084fc","strokeWidth":2,"cornerRadius":12},
          {"id":"c3-num","kind":"circle","position":{"x":160,"y":810},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#a855f7"},
          {"id":"c3-numt","kind":"text","position":{"x":146,"y":785},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"3","fontSize":42,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"c3-t1","kind":"text","position":{"x":230,"y":770},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Scrub for 20 seconds","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#581c87","width":900,"align":"left"},
          {"id":"c3-t2","kind":"text","position":{"x":230,"y":820},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"That's about two rounds of “Happy Birthday”.","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#6b21a8","width":900,"align":"left"},

          {"id":"card-4","kind":"rect","position":{"x":80,"y":920},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":140,"fill":"#ffffff","stroke":"#c084fc","strokeWidth":2,"cornerRadius":12},
          {"id":"c4-num","kind":"circle","position":{"x":160,"y":990},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#a855f7"},
          {"id":"c4-numt","kind":"text","position":{"x":146,"y":965},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"4","fontSize":42,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"c4-t1","kind":"text","position":{"x":230,"y":950},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Rinse and dry","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#581c87","width":900,"align":"left"},
          {"id":"c4-t2","kind":"text","position":{"x":230,"y":1000},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Use a clean towel or air-dry thoroughly.","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#6b21a8","width":900,"align":"left"},

          {"id":"big-time","kind":"text","position":{"x":80,"y":1180},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"20","fontSize":300,"fontFamily":"Inter, sans-serif",
           "fill":"#a855f7","width":600,"align":"left"},
          {"id":"big-time-u","kind":"text","position":{"x":80,"y":1500},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"seconds is all it takes","fontSize":40,
           "fontFamily":"Inter, sans-serif","fill":"#581c87","width":1080,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1700},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"If soap and water are not available, use a hand sanitizer with at least 60% alcohol.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#57534e","width":1080,"align":"left"}
        ]
        """);

    private static string AllergySeasonCanvas() => Canvas(
        name: "Spring Allergy Survival Kit",
        background: "#fdf2f8",
        shapes: """
        [
          {"id":"dot-1","kind":"circle","position":{"x":120,"y":140},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.6,"draggable":true,"zIndex":1,
           "radius":18,"fill":"#f9a8d4"},
          {"id":"dot-2","kind":"circle","position":{"x":1100,"y":120},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.6,"draggable":true,"zIndex":1,
           "radius":24,"fill":"#fb7185"},
          {"id":"dot-3","kind":"circle","position":{"x":80,"y":1640},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.6,"draggable":true,"zIndex":1,
           "radius":20,"fill":"#fbcfe8"},

          {"id":"head","kind":"text","position":{"x":80,"y":220},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"Spring Allergy Survival Kit","fontSize":84,
           "fontFamily":"Inter, sans-serif","fill":"#9f1239","width":1080,"align":"left"},

          {"id":"sub","kind":"text","position":{"x":80,"y":460},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"Everything you need under one roof","fontSize":34,
           "fontFamily":"Inter, sans-serif","fill":"#be185d","width":1080,"align":"left"},

          {"id":"card-1","kind":"rect","position":{"x":80,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":320,"height":420,"fill":"#ffffff","stroke":"#fbcfe8","strokeWidth":2,"cornerRadius":16},
          {"id":"card-1-icon","kind":"circle","position":{"x":240,"y":760},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":60,"fill":"#fce7f3"},
          {"id":"card-1-t","kind":"text","position":{"x":100,"y":920},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Antihistamines","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#831843","width":280,"align":"center"},
          {"id":"card-1-s","kind":"text","position":{"x":100,"y":970},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"24-hour relief","fontSize":20,"fontFamily":"Inter, sans-serif",
           "fill":"#9f1239","width":280,"align":"center"},

          {"id":"card-2","kind":"rect","position":{"x":460,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":320,"height":420,"fill":"#ffffff","stroke":"#fbcfe8","strokeWidth":2,"cornerRadius":16},
          {"id":"card-2-icon","kind":"circle","position":{"x":620,"y":760},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":60,"fill":"#fce7f3"},
          {"id":"card-2-t","kind":"text","position":{"x":480,"y":920},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Nasal Sprays","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#831843","width":280,"align":"center"},
          {"id":"card-2-s","kind":"text","position":{"x":480,"y":970},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Targeted relief","fontSize":20,"fontFamily":"Inter, sans-serif",
           "fill":"#9f1239","width":280,"align":"center"},

          {"id":"card-3","kind":"rect","position":{"x":840,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":320,"height":420,"fill":"#ffffff","stroke":"#fbcfe8","strokeWidth":2,"cornerRadius":16},
          {"id":"card-3-icon","kind":"circle","position":{"x":1000,"y":760},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":60,"fill":"#fce7f3"},
          {"id":"card-3-t","kind":"text","position":{"x":860,"y":920},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Eye Drops","fontSize":32,"fontFamily":"Inter, sans-serif",
           "fill":"#831843","width":280,"align":"center"},
          {"id":"card-3-s","kind":"text","position":{"x":860,"y":970},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Soothing comfort","fontSize":20,"fontFamily":"Inter, sans-serif",
           "fill":"#9f1239","width":280,"align":"center"},

          {"id":"banner","kind":"rect","position":{"x":80,"y":1280},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":260,"fill":"#9f1239","cornerRadius":20},
          {"id":"banner-t","kind":"text","position":{"x":140,"y":1330},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Bundle and save 15%","fontSize":54,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":960,"align":"left"},
          {"id":"banner-s","kind":"text","position":{"x":140,"y":1420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"On any combination of allergy products. Ask the pharmacist.","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#fce7f3","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1700},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Always read the label. Talk to your pharmacist if you take other medications.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#57534e","width":1080,"align":"left"}
        ]
        """);

    private static string WelcomeCanvas() => Canvas(
        name: "New Patient Welcome",
        background: "#f8fafc",
        shapes: """
        [
          {"id":"hdr-bar","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":180,"fill":"#0f172a"},

          {"id":"hdr-1","kind":"text","position":{"x":80,"y":50},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"Welcome to {{pharmacy_name}}","fontSize":52,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":1080,"align":"left"},
          {"id":"hdr-2","kind":"text","position":{"x":80,"y":120},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"Your community pharmacy","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#cbd5e1","width":1080,"align":"left"},

          {"id":"main","kind":"text","position":{"x":80,"y":280},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"We're glad you're here.","fontSize":80,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":1080,"align":"left"},

          {"id":"main-sub","kind":"text","position":{"x":80,"y":420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Three things we do best for new patients:","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"},

          {"id":"svc-1","kind":"rect","position":{"x":80,"y":520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":340,"height":300,"fill":"#e0f2fe","cornerRadius":16},
          {"id":"svc-1-t","kind":"text","position":{"x":110,"y":740},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Prescriptions","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#075985","width":280,"align":"left"},
          {"id":"svc-1-s","kind":"text","position":{"x":110,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Easy transfers in 24 hours","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":280,"align":"left"},

          {"id":"svc-2","kind":"rect","position":{"x":450,"y":520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":340,"height":300,"fill":"#fef3c7","cornerRadius":16},
          {"id":"svc-2-t","kind":"text","position":{"x":480,"y":740},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Vaccinations","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#78350f","width":280,"align":"left"},
          {"id":"svc-2-s","kind":"text","position":{"x":480,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Walk-ins, daily","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#92400e","width":280,"align":"left"},

          {"id":"svc-3","kind":"rect","position":{"x":820,"y":520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":340,"height":300,"fill":"#d1fae5","cornerRadius":16},
          {"id":"svc-3-t","kind":"text","position":{"x":850,"y":740},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Consultations","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#065f46","width":280,"align":"left"},
          {"id":"svc-3-s","kind":"text","position":{"x":850,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Free 5-minute chats","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#064e3b","width":280,"align":"left"},

          {"id":"hero-img","kind":"image","position":{"x":240,"y":870},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "blobKey":"https://images.unsplash.com/photo-1576091160550-2173dba999ef?w=1000&q=80&auto=format&fit=crop",
           "width":760,"height":180},

          {"id":"contact","kind":"rect","position":{"x":80,"y":1080},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":380,"fill":"#0f172a","cornerRadius":16},
          {"id":"contact-1","kind":"text","position":{"x":140,"y":1140},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Visit us","fontSize":56,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":960,"align":"left"},
          {"id":"contact-2","kind":"text","position":{"x":140,"y":1240},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"{{address}}","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#cbd5e1","width":960,"align":"left"},
          {"id":"contact-3","kind":"text","position":{"x":140,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"{{phone}}","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#cbd5e1","width":960,"align":"left"},
          {"id":"contact-4","kind":"text","position":{"x":140,"y":1360},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Open daily • 9am – 9pm","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#94a3b8","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"All licensed pharmacists. State license #ACME-123456. Member of the National Community Pharmacists Association.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string CovidBoosterCanvas() => Canvas(
        name: "Autumn COVID-19 Booster",
        background: "#f0f9ff",
        shapes: """
        [
          {"id":"top-bar","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":160,"fill":"#0c4a6e"},

          {"id":"hdr-tag","kind":"rect","position":{"x":80,"y":50},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "width":260,"height":48,"fill":"#10b981","cornerRadius":24},
          {"id":"hdr-tag-t","kind":"text","position":{"x":92,"y":58},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"AUTUMN CAMPAIGN","fontSize":24,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":236,"align":"center"},
          {"id":"hdr-t","kind":"text","position":{"x":380,"y":58},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"{{pharmacy_name}}","fontSize":28,"fontFamily":"Inter, sans-serif",
           "fill":"#bae6fd","width":800,"align":"left"},

          {"id":"hero-blob","kind":"circle","position":{"x":1080,"y":420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.18,"draggable":true,"zIndex":2,
           "radius":280,"fill":"#0ea5e9"},

          {"id":"headline","kind":"text","position":{"x":80,"y":230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"COVID-19","fontSize":120,"fontFamily":"Inter, sans-serif",
           "fill":"#0c4a6e","width":1080,"align":"left"},
          {"id":"headline2","kind":"text","position":{"x":80,"y":380},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Booster Available","fontSize":92,"fontFamily":"Inter, sans-serif",
           "fill":"#0369a1","width":1080,"align":"left"},
          {"id":"sub","kind":"text","position":{"x":80,"y":520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Stay protected this winter — appointments now open","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#075985","width":1080,"align":"left"},

          {"id":"divider","kind":"line","position":{"x":80,"y":620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "points":[0,0,1080,0],"stroke":"#0ea5e9","strokeWidth":4},

          {"id":"col-h-1","kind":"text","position":{"x":80,"y":680},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Who can book","fontSize":34,"fontFamily":"Inter, sans-serif",
           "fill":"#0c4a6e","width":520,"align":"left"},
          {"id":"col-1-a","kind":"text","position":{"x":80,"y":740},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Adults aged 65 and over","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},
          {"id":"col-1-b","kind":"text","position":{"x":80,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Anyone in a clinical risk group","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},
          {"id":"col-1-c","kind":"text","position":{"x":80,"y":840},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Frontline health & care workers","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},
          {"id":"col-1-d","kind":"text","position":{"x":80,"y":890},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Carers of at-risk individuals","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},

          {"id":"col-h-2","kind":"text","position":{"x":640,"y":680},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"What to bring","fontSize":34,"fontFamily":"Inter, sans-serif",
           "fill":"#0c4a6e","width":520,"align":"left"},
          {"id":"col-2-a","kind":"text","position":{"x":640,"y":740},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  Photo ID","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},
          {"id":"col-2-b","kind":"text","position":{"x":640,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  NHS number (if known)","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},
          {"id":"col-2-c","kind":"text","position":{"x":640,"y":840},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  List of current medications","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},
          {"id":"col-2-d","kind":"text","position":{"x":640,"y":890},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"•  Short-sleeve top is helpful","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":520,"align":"left"},

          {"id":"safe-row","kind":"rect","position":{"x":80,"y":1010},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":160,"fill":"#ecfeff","stroke":"#a5f3fc","strokeWidth":2,"cornerRadius":16},
          {"id":"safe-1","kind":"text","position":{"x":120,"y":1050},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"15 min","fontSize":42,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":320,"align":"center"},
          {"id":"safe-1-s","kind":"text","position":{"x":120,"y":1110},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Total appointment","fontSize":20,"fontFamily":"Inter, sans-serif",
           "fill":"#155e75","width":320,"align":"center"},
          {"id":"safe-2","kind":"text","position":{"x":460,"y":1050},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Free","fontSize":42,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":320,"align":"center"},
          {"id":"safe-2-s","kind":"text","position":{"x":460,"y":1110},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"On the NHS, if eligible","fontSize":20,"fontFamily":"Inter, sans-serif",
           "fill":"#155e75","width":320,"align":"center"},
          {"id":"safe-3","kind":"text","position":{"x":800,"y":1050},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"7 days","fontSize":42,"fontFamily":"Inter, sans-serif",
           "fill":"#0e7490","width":320,"align":"center"},
          {"id":"safe-3-s","kind":"text","position":{"x":800,"y":1110},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Walk-ins available","fontSize":20,"fontFamily":"Inter, sans-serif",
           "fill":"#155e75","width":320,"align":"center"},

          {"id":"cta-box","kind":"rect","position":{"x":80,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1080,"height":260,"fill":"#0c4a6e","cornerRadius":20},
          {"id":"cta-accent","kind":"rect","position":{"x":80,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":12,"height":260,"fill":"#10b981"},
          {"id":"cta-title","kind":"text","position":{"x":140,"y":1280},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Book your booster","fontSize":62,"fontFamily":"Inter, sans-serif",
           "fill":"#ffffff","width":960,"align":"left"},
          {"id":"cta-sub","kind":"text","position":{"x":140,"y":1380},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Scan the QR code or call {{phone}} to reserve a slot","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#bae6fd","width":960,"align":"left"},
          {"id":"cta-line","kind":"text","position":{"x":140,"y":1430},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{address}}","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#7dd3fc","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1680},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"For eligibility and contraindications, speak to our pharmacist. Always read the patient information leaflet before vaccination.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string DiabetesAwarenessCanvas() => Canvas(
        name: "Diabetes Awareness",
        background: "#eff6ff",
        shapes: """
        [
          {"id":"corner","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":480,"height":160,"fill":"#1d4ed8"},
          {"id":"corner-t","kind":"text","position":{"x":40,"y":58},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"AWARENESS · 14 NOV","fontSize":26,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":420,"align":"left"},

          {"id":"big-stat","kind":"text","position":{"x":80,"y":220},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"1 in 11","fontSize":190,"fontFamily":"Inter, sans-serif",
           "fill":"#1e3a8a","width":1080,"align":"left"},
          {"id":"big-stat-2","kind":"text","position":{"x":80,"y":450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"adults live with diabetes","fontSize":48,
           "fontFamily":"Inter, sans-serif","fill":"#1e40af","width":1080,"align":"left"},
          {"id":"big-stat-3","kind":"text","position":{"x":80,"y":520},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Many don't know they have it.","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#3730a3","width":1080,"align":"left"},

          {"id":"know-h","kind":"text","position":{"x":80,"y":660},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Know the signs","fontSize":56,"fontFamily":"Inter, sans-serif",
           "fill":"#1e3a8a","width":1080,"align":"left"},

          {"id":"sym-1-c","kind":"rect","position":{"x":80,"y":770},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":110,"fill":"#ffffff","stroke":"#dbeafe","strokeWidth":2,"cornerRadius":14},
          {"id":"sym-1-d","kind":"circle","position":{"x":140,"y":825},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":22,"fill":"#3b82f6"},
          {"id":"sym-1-t","kind":"text","position":{"x":200,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Excessive thirst","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},
          {"id":"sym-1-s","kind":"text","position":{"x":200,"y":830},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Drinking far more than usual","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#475569","width":380,"align":"left"},

          {"id":"sym-2-c","kind":"rect","position":{"x":640,"y":770},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":110,"fill":"#ffffff","stroke":"#dbeafe","strokeWidth":2,"cornerRadius":14},
          {"id":"sym-2-d","kind":"circle","position":{"x":700,"y":825},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":22,"fill":"#3b82f6"},
          {"id":"sym-2-t","kind":"text","position":{"x":760,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Frequent urination","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},
          {"id":"sym-2-s","kind":"text","position":{"x":760,"y":830},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Especially through the night","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#475569","width":380,"align":"left"},

          {"id":"sym-3-c","kind":"rect","position":{"x":80,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":110,"fill":"#ffffff","stroke":"#dbeafe","strokeWidth":2,"cornerRadius":14},
          {"id":"sym-3-d","kind":"circle","position":{"x":140,"y":955},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":22,"fill":"#3b82f6"},
          {"id":"sym-3-t","kind":"text","position":{"x":200,"y":920},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Unexplained tiredness","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},
          {"id":"sym-3-s","kind":"text","position":{"x":200,"y":960},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Persistent low energy levels","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#475569","width":380,"align":"left"},

          {"id":"sym-4-c","kind":"rect","position":{"x":640,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":110,"fill":"#ffffff","stroke":"#dbeafe","strokeWidth":2,"cornerRadius":14},
          {"id":"sym-4-d","kind":"circle","position":{"x":700,"y":955},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":22,"fill":"#3b82f6"},
          {"id":"sym-4-t","kind":"text","position":{"x":760,"y":920},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Blurred vision","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},
          {"id":"sym-4-s","kind":"text","position":{"x":760,"y":960},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Or slow-healing cuts","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#475569","width":380,"align":"left"},

          {"id":"cta-bg","kind":"rect","position":{"x":80,"y":1100},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":420,"fill":"#1d4ed8","cornerRadius":24},
          {"id":"cta-glow","kind":"circle","position":{"x":1020,"y":1340},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.3,"draggable":true,"zIndex":4,
           "radius":160,"fill":"#60a5fa"},
          {"id":"cta-pill","kind":"rect","position":{"x":140,"y":1150},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":180,"height":40,"fill":"#10b981","cornerRadius":20},
          {"id":"cta-pill-t","kind":"text","position":{"x":156,"y":1158},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"FREE CHECK","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":148,"align":"center"},
          {"id":"cta-h","kind":"text","position":{"x":140,"y":1220},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"5-minute diabetes risk check","fontSize":52,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":960,"align":"left"},
          {"id":"cta-s","kind":"text","position":{"x":140,"y":1340},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Our pharmacists offer free in-store blood glucose checks during awareness month. No appointment needed.",
           "fontSize":24,"fontFamily":"Inter, sans-serif","fill":"#dbeafe","width":860,"align":"left"},
          {"id":"cta-c","kind":"text","position":{"x":140,"y":1450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"{{phone}}  •  {{address}}","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#bfdbfe","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"This information is for guidance only and not a substitute for medical advice. If you are concerned about diabetes, speak to your GP.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string RepeatPrescriptionCanvas() => Canvas(
        name: "Repeat Prescription Service",
        background: "#f0fdf4",
        shapes: """
        [
          {"id":"hero-bg","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":620,"fill":"#065f46"},
          {"id":"hero-blob","kind":"circle","position":{"x":1140,"y":300},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.22,"draggable":true,"zIndex":2,
           "radius":320,"fill":"#10b981"},

          {"id":"chip","kind":"rect","position":{"x":80,"y":100},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":290,"height":44,"fill":"#10b981","cornerRadius":22},
          {"id":"chip-t","kind":"text","position":{"x":96,"y":108},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"FREE NHS SERVICE","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":258,"align":"center"},

          {"id":"head","kind":"text","position":{"x":80,"y":190},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Never run out of","fontSize":78,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":1080,"align":"left"},
          {"id":"head-2","kind":"text","position":{"x":80,"y":300},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"your medication","fontSize":78,
           "fontFamily":"Inter, sans-serif","fill":"#a7f3d0","width":1080,"align":"left"},

          {"id":"hero-sub","kind":"text","position":{"x":80,"y":440},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Sign up for our managed repeat prescription service.","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#d1fae5","width":1080,"align":"left"},
          {"id":"hero-sub-2","kind":"text","position":{"x":80,"y":490},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"We'll order, dispense, and even deliver to your door.","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#d1fae5","width":1080,"align":"left"},

          {"id":"step-h","kind":"text","position":{"x":80,"y":700},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"How it works","fontSize":48,
           "fontFamily":"Inter, sans-serif","fill":"#065f46","width":1080,"align":"left"},

          {"id":"s1-c","kind":"rect","position":{"x":80,"y":810},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":340,"height":340,"fill":"#ffffff","stroke":"#bbf7d0","strokeWidth":2,"cornerRadius":18},
          {"id":"s1-n","kind":"circle","position":{"x":140,"y":870},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":34,"fill":"#10b981"},
          {"id":"s1-nt","kind":"text","position":{"x":128,"y":845},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"1","fontSize":40,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"s1-t","kind":"text","position":{"x":110,"y":960},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Sign up once","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#065f46","width":280,"align":"left"},
          {"id":"s1-s","kind":"text","position":{"x":110,"y":1020},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Pop in for a 2-minute registration with our team.","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#047857","width":280,"align":"left"},

          {"id":"s2-c","kind":"rect","position":{"x":450,"y":810},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":340,"height":340,"fill":"#ffffff","stroke":"#bbf7d0","strokeWidth":2,"cornerRadius":18},
          {"id":"s2-n","kind":"circle","position":{"x":510,"y":870},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":34,"fill":"#10b981"},
          {"id":"s2-nt","kind":"text","position":{"x":498,"y":845},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"2","fontSize":40,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"s2-t","kind":"text","position":{"x":480,"y":960},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"We order it","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#065f46","width":280,"align":"left"},
          {"id":"s2-s","kind":"text","position":{"x":480,"y":1020},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"We request your repeat from your GP 5 days early.","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#047857","width":280,"align":"left"},

          {"id":"s3-c","kind":"rect","position":{"x":820,"y":810},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":340,"height":340,"fill":"#ffffff","stroke":"#bbf7d0","strokeWidth":2,"cornerRadius":18},
          {"id":"s3-n","kind":"circle","position":{"x":880,"y":870},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":34,"fill":"#10b981"},
          {"id":"s3-nt","kind":"text","position":{"x":868,"y":845},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"3","fontSize":40,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"s3-t","kind":"text","position":{"x":850,"y":960},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Collect or deliver","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#065f46","width":280,"align":"left"},
          {"id":"s3-s","kind":"text","position":{"x":850,"y":1020},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Pick it up in-store or have it delivered free.","fontSize":20,
           "fontFamily":"Inter, sans-serif","fill":"#047857","width":280,"align":"left"},

          {"id":"who-h","kind":"text","position":{"x":80,"y":1210},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Especially useful if you:","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#065f46","width":1080,"align":"left"},
          {"id":"who-1","kind":"text","position":{"x":80,"y":1270},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Take medication regularly for a long-term condition","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#1f2937","width":1080,"align":"left"},
          {"id":"who-2","kind":"text","position":{"x":80,"y":1310},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Look after a family member's prescriptions","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#1f2937","width":1080,"align":"left"},
          {"id":"who-3","kind":"text","position":{"x":80,"y":1350},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"✓  Find ordering from your GP fiddly or time-consuming","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#1f2937","width":1080,"align":"left"},

          {"id":"cta","kind":"rect","position":{"x":80,"y":1430},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1080,"height":160,"fill":"#065f46","cornerRadius":20},
          {"id":"cta-t","kind":"text","position":{"x":140,"y":1470},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Sign up at the counter today","fontSize":42,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":960,"align":"left"},
          {"id":"cta-s","kind":"text","position":{"x":140,"y":1530},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"{{phone}}  •  {{address}}","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#a7f3d0","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Subject to GP approval. Delivery available within a 3-mile radius. Speak to the pharmacy team for full terms.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string TravelClinicCanvas() => Canvas(
        name: "Travel Health Clinic",
        background: "#fff7ed",
        shapes: """
        [
          {"id":"side-bar","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":18,"height":1754,"fill":"#ea580c"},

          {"id":"badge","kind":"rect","position":{"x":80,"y":100},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":260,"height":44,"fill":"#9a3412","cornerRadius":22},
          {"id":"badge-t","kind":"text","position":{"x":96,"y":108},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"TRAVEL CLINIC","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":228,"align":"center"},

          {"id":"head","kind":"text","position":{"x":80,"y":190},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Travelling","fontSize":118,"fontFamily":"Inter, sans-serif",
           "fill":"#7c2d12","width":1080,"align":"left"},
          {"id":"head-2","kind":"text","position":{"x":80,"y":330},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"abroad?","fontSize":118,"fontFamily":"Inter, sans-serif",
           "fill":"#ea580c","width":1080,"align":"left"},

          {"id":"hero-sub","kind":"text","position":{"x":80,"y":490},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Book a travel health consultation 6-8 weeks before you fly.",
           "fontSize":30,"fontFamily":"Inter, sans-serif","fill":"#9a3412","width":1080,"align":"left"},

          {"id":"divider","kind":"line","position":{"x":80,"y":590},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "points":[0,0,1080,0],"stroke":"#fdba74","strokeWidth":3},

          {"id":"svc-h","kind":"text","position":{"x":80,"y":640},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"What we offer","fontSize":42,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":1080,"align":"left"},

          {"id":"v-1","kind":"rect","position":{"x":80,"y":720},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#fed7aa","strokeWidth":2,"cornerRadius":14},
          {"id":"v-1-icon","kind":"circle","position":{"x":150,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#fb923c"},
          {"id":"v-1-t","kind":"text","position":{"x":220,"y":750},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Travel vaccines","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":360,"align":"left"},
          {"id":"v-1-s","kind":"text","position":{"x":220,"y":800},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Hep A & B, Typhoid, Yellow Fever, Rabies","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#9a3412","width":360,"align":"left"},

          {"id":"v-2","kind":"rect","position":{"x":640,"y":720},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#fed7aa","strokeWidth":2,"cornerRadius":14},
          {"id":"v-2-icon","kind":"circle","position":{"x":710,"y":790},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#fb923c"},
          {"id":"v-2-t","kind":"text","position":{"x":780,"y":750},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Antimalarials","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":360,"align":"left"},
          {"id":"v-2-s","kind":"text","position":{"x":780,"y":800},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Same-day private prescription","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#9a3412","width":360,"align":"left"},

          {"id":"v-3","kind":"rect","position":{"x":80,"y":880},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#fed7aa","strokeWidth":2,"cornerRadius":14},
          {"id":"v-3-icon","kind":"circle","position":{"x":150,"y":950},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#fb923c"},
          {"id":"v-3-t","kind":"text","position":{"x":220,"y":910},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Travel medicine kits","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":360,"align":"left"},
          {"id":"v-3-s","kind":"text","position":{"x":220,"y":960},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Pre-packed for tummy upsets, allergies","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#9a3412","width":360,"align":"left"},

          {"id":"v-4","kind":"rect","position":{"x":640,"y":880},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#fed7aa","strokeWidth":2,"cornerRadius":14},
          {"id":"v-4-icon","kind":"circle","position":{"x":710,"y":950},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#fb923c"},
          {"id":"v-4-t","kind":"text","position":{"x":780,"y":910},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Destination advice","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":360,"align":"left"},
          {"id":"v-4-s","kind":"text","position":{"x":780,"y":960},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Risk briefing for 200+ countries","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#9a3412","width":360,"align":"left"},

          {"id":"globe-h","kind":"text","position":{"x":80,"y":1080},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Popular destinations we cover","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":1080,"align":"left"},
          {"id":"globe-tags","kind":"text","position":{"x":80,"y":1130},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Thailand   •   India   •   Kenya   •   Brazil   •   Vietnam   •   Egypt   •   Peru",
           "fontSize":22,"fontFamily":"Inter, sans-serif","fill":"#9a3412","width":1080,"align":"left"},

          {"id":"price","kind":"rect","position":{"x":80,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":160,"fill":"#ffedd5","stroke":"#fdba74","strokeWidth":2,"cornerRadius":16},
          {"id":"price-t","kind":"text","position":{"x":110,"y":1260},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"From £25","fontSize":52,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":460,"align":"left"},
          {"id":"price-s","kind":"text","position":{"x":110,"y":1340},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"per traveller consultation","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#9a3412","width":460,"align":"left"},

          {"id":"time","kind":"rect","position":{"x":640,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":160,"fill":"#ffedd5","stroke":"#fdba74","strokeWidth":2,"cornerRadius":16},
          {"id":"time-t","kind":"text","position":{"x":670,"y":1260},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"30 min","fontSize":52,
           "fontFamily":"Inter, sans-serif","fill":"#7c2d12","width":460,"align":"left"},
          {"id":"time-s","kind":"text","position":{"x":670,"y":1340},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"average appointment","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#9a3412","width":460,"align":"left"},

          {"id":"cta","kind":"rect","position":{"x":80,"y":1440},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1080,"height":160,"fill":"#ea580c","cornerRadius":20},
          {"id":"cta-t","kind":"text","position":{"x":140,"y":1480},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Book online or call {{phone}}","fontSize":42,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":960,"align":"left"},
          {"id":"cta-s","kind":"text","position":{"x":140,"y":1540},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"{{address}}","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#ffedd5","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"All vaccines administered by a GPhC-registered pharmacist. Yellow fever certificate issued where eligible.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string StopSmokingCanvas() => Canvas(
        name: "Stop Smoking Support",
        background: "#fef2f2",
        shapes: """
        [
          {"id":"top-block","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":540,"fill":"#7f1d1d"},
          {"id":"top-glow","kind":"circle","position":{"x":1080,"y":420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.25,"draggable":true,"zIndex":2,
           "radius":280,"fill":"#f87171"},

          {"id":"tag","kind":"rect","position":{"x":80,"y":80},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":260,"height":44,"fill":"#fbbf24","cornerRadius":22},
          {"id":"tag-t","kind":"text","position":{"x":96,"y":88},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"STOPTOBER READY","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":228,"align":"center"},

          {"id":"head","kind":"text","position":{"x":80,"y":170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Ready to quit?","fontSize":110,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":1080,"align":"left"},
          {"id":"head-2","kind":"text","position":{"x":80,"y":310},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"We're with you every step.","fontSize":44,
           "fontFamily":"Inter, sans-serif","fill":"#fecaca","width":1080,"align":"left"},

          {"id":"stat-c","kind":"rect","position":{"x":80,"y":420},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":80,"fill":"#fbbf24","cornerRadius":12},
          {"id":"stat-t","kind":"text","position":{"x":120,"y":440},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"You're 3× more likely to quit with pharmacy support.",
           "fontSize":30,"fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":1000,"align":"left"},

          {"id":"how-h","kind":"text","position":{"x":80,"y":600},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"How we help","fontSize":50,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":1080,"align":"left"},

          {"id":"h-1","kind":"rect","position":{"x":80,"y":700},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":120,"fill":"#ffffff","stroke":"#fecaca","strokeWidth":2,"cornerRadius":14},
          {"id":"h-1-n","kind":"circle","position":{"x":150,"y":760},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#dc2626"},
          {"id":"h-1-nt","kind":"text","position":{"x":138,"y":735},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"1","fontSize":40,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"h-1-t","kind":"text","position":{"x":220,"y":720},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Free initial consultation","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":900,"align":"left"},
          {"id":"h-1-s","kind":"text","position":{"x":220,"y":770},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"15 minutes with a trained advisor — completely confidential.",
           "fontSize":20,"fontFamily":"Inter, sans-serif","fill":"#991b1b","width":900,"align":"left"},

          {"id":"h-2","kind":"rect","position":{"x":80,"y":840},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":120,"fill":"#ffffff","stroke":"#fecaca","strokeWidth":2,"cornerRadius":14},
          {"id":"h-2-n","kind":"circle","position":{"x":150,"y":900},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#dc2626"},
          {"id":"h-2-nt","kind":"text","position":{"x":138,"y":875},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"2","fontSize":40,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"h-2-t","kind":"text","position":{"x":220,"y":860},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"NRT — patches, gum, lozenges","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":900,"align":"left"},
          {"id":"h-2-s","kind":"text","position":{"x":220,"y":910},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"We'll pick the right product and strength for you.",
           "fontSize":20,"fontFamily":"Inter, sans-serif","fill":"#991b1b","width":900,"align":"left"},

          {"id":"h-3","kind":"rect","position":{"x":80,"y":980},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":120,"fill":"#ffffff","stroke":"#fecaca","strokeWidth":2,"cornerRadius":14},
          {"id":"h-3-n","kind":"circle","position":{"x":150,"y":1040},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":36,"fill":"#dc2626"},
          {"id":"h-3-nt","kind":"text","position":{"x":138,"y":1015},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"3","fontSize":40,"fontFamily":"Inter, sans-serif","fill":"#ffffff"},
          {"id":"h-3-t","kind":"text","position":{"x":220,"y":1000},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Weekly check-ins for 12 weeks","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":900,"align":"left"},
          {"id":"h-3-s","kind":"text","position":{"x":220,"y":1050},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"CO breath tests, encouragement, and craving strategies.",
           "fontSize":20,"fontFamily":"Inter, sans-serif","fill":"#991b1b","width":900,"align":"left"},

          {"id":"benefit-h","kind":"text","position":{"x":80,"y":1170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"After you quit, your body recovers fast.","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":1080,"align":"left"},

          {"id":"bn-1","kind":"text","position":{"x":80,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"20 minutes","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#dc2626","width":300,"align":"left"},
          {"id":"bn-1-s","kind":"text","position":{"x":340,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Heart rate drops to normal","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":800,"align":"left"},

          {"id":"bn-2","kind":"text","position":{"x":80,"y":1275},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"12 hours","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#dc2626","width":300,"align":"left"},
          {"id":"bn-2-s","kind":"text","position":{"x":340,"y":1275},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Carbon monoxide leaves your blood","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":800,"align":"left"},

          {"id":"bn-3","kind":"text","position":{"x":80,"y":1320},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"1 year","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#dc2626","width":300,"align":"left"},
          {"id":"bn-3-s","kind":"text","position":{"x":340,"y":1320},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Heart disease risk is halved","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":800,"align":"left"},

          {"id":"cta","kind":"rect","position":{"x":80,"y":1410},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1080,"height":200,"fill":"#7f1d1d","cornerRadius":20},
          {"id":"cta-accent","kind":"rect","position":{"x":80,"y":1410},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "width":12,"height":200,"fill":"#fbbf24"},
          {"id":"cta-t","kind":"text","position":{"x":140,"y":1450},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Talk to us today","fontSize":50,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":960,"align":"left"},
          {"id":"cta-s","kind":"text","position":{"x":140,"y":1530},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Walk in or call {{phone}}",
           "fontSize":22,"fontFamily":"Inter, sans-serif","fill":"#fecaca","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":7,
           "text":"NHS-funded service for eligible patients. Suitability assessed by our pharmacist. NRT not suitable for some conditions — read the leaflet.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string ChildrensCoughColdCanvas() => Canvas(
        name: "Children's Cough & Cold",
        background: "#eff6ff",
        shapes: """
        [
          {"id":"sky","kind":"rect","position":{"x":0,"y":0},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":1,
           "width":1240,"height":420,"fill":"#bfdbfe"},
          {"id":"cloud-1","kind":"circle","position":{"x":1100,"y":110},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.7,"draggable":true,"zIndex":2,
           "radius":70,"fill":"#ffffff"},
          {"id":"cloud-2","kind":"circle","position":{"x":1180,"y":140},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.7,"draggable":true,"zIndex":2,
           "radius":50,"fill":"#ffffff"},
          {"id":"cloud-3","kind":"circle","position":{"x":120,"y":300},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":0.6,"draggable":true,"zIndex":2,
           "radius":40,"fill":"#ffffff"},

          {"id":"tag","kind":"rect","position":{"x":80,"y":80},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":280,"height":44,"fill":"#1d4ed8","cornerRadius":22},
          {"id":"tag-t","kind":"text","position":{"x":96,"y":88},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"WINTER HEALTH","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":248,"align":"center"},

          {"id":"head","kind":"text","position":{"x":80,"y":170},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Cough & Cold","fontSize":92,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":1080,"align":"left"},
          {"id":"head-2","kind":"text","position":{"x":80,"y":280},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Safe relief for under-12s","fontSize":42,
           "fontFamily":"Inter, sans-serif","fill":"#1d4ed8","width":1080,"align":"left"},

          {"id":"do-h","kind":"text","position":{"x":80,"y":480},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"What to try first","fontSize":40,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":1080,"align":"left"},

          {"id":"do-1","kind":"rect","position":{"x":80,"y":560},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#bfdbfe","strokeWidth":2,"cornerRadius":14},
          {"id":"do-1-d","kind":"circle","position":{"x":150,"y":620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":28,"fill":"#22c55e"},
          {"id":"do-1-t","kind":"text","position":{"x":210,"y":585},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Plenty of fluids","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":380,"align":"left"},
          {"id":"do-1-s","kind":"text","position":{"x":210,"y":625},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Water, diluted juice, warm honey & lemon (1+)","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},

          {"id":"do-2","kind":"rect","position":{"x":640,"y":560},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#bfdbfe","strokeWidth":2,"cornerRadius":14},
          {"id":"do-2-d","kind":"circle","position":{"x":710,"y":620},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":28,"fill":"#22c55e"},
          {"id":"do-2-t","kind":"text","position":{"x":770,"y":585},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Rest & sleep","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":380,"align":"left"},
          {"id":"do-2-s","kind":"text","position":{"x":770,"y":625},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Prop with an extra pillow for blocked noses","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},

          {"id":"do-3","kind":"rect","position":{"x":80,"y":720},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#bfdbfe","strokeWidth":2,"cornerRadius":14},
          {"id":"do-3-d","kind":"circle","position":{"x":150,"y":780},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":28,"fill":"#22c55e"},
          {"id":"do-3-t","kind":"text","position":{"x":210,"y":745},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Paracetamol or ibuprofen","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":380,"align":"left"},
          {"id":"do-3-s","kind":"text","position":{"x":210,"y":785},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"For fever & aches — never both at the same time","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},

          {"id":"do-4","kind":"rect","position":{"x":640,"y":720},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":520,"height":140,"fill":"#ffffff","stroke":"#bfdbfe","strokeWidth":2,"cornerRadius":14},
          {"id":"do-4-d","kind":"circle","position":{"x":710,"y":780},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "radius":28,"fill":"#22c55e"},
          {"id":"do-4-t","kind":"text","position":{"x":770,"y":745},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Saline nose drops","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#0c4a6e","width":380,"align":"left"},
          {"id":"do-4-s","kind":"text","position":{"x":770,"y":785},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Safe from birth — gentle decongestion","fontSize":18,
           "fontFamily":"Inter, sans-serif","fill":"#1e3a8a","width":380,"align":"left"},

          {"id":"warn-h","kind":"text","position":{"x":80,"y":920},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"Call your GP or 111 if your child:","fontSize":32,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":1080,"align":"left"},

          {"id":"w-1","kind":"text","position":{"x":80,"y":990},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"⚠  Has a fever above 38°C and is under 3 months old","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":1080,"align":"left"},
          {"id":"w-2","kind":"text","position":{"x":80,"y":1040},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"⚠  Is wheezing, struggling to breathe, or unusually drowsy","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":1080,"align":"left"},
          {"id":"w-3","kind":"text","position":{"x":80,"y":1090},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"⚠  Has symptoms lasting longer than 3 weeks","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":1080,"align":"left"},
          {"id":"w-4","kind":"text","position":{"x":80,"y":1140},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "text":"⚠  Develops a rash that doesn't fade under pressure","fontSize":24,
           "fontFamily":"Inter, sans-serif","fill":"#0f172a","width":1080,"align":"left"},

          {"id":"avoid","kind":"rect","position":{"x":80,"y":1230},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":3,
           "width":1080,"height":140,"fill":"#fee2e2","stroke":"#fca5a5","strokeWidth":2,"cornerRadius":14},
          {"id":"avoid-h","kind":"text","position":{"x":120,"y":1260},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Avoid:","fontSize":28,
           "fontFamily":"Inter, sans-serif","fill":"#7f1d1d","width":1000,"align":"left"},
          {"id":"avoid-s","kind":"text","position":{"x":120,"y":1310},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"Cough & cold remedies for under-6s · Aspirin for under-16s · Honey for under-1s",
           "fontSize":20,"fontFamily":"Inter, sans-serif","fill":"#991b1b","width":1000,"align":"left"},

          {"id":"cta","kind":"rect","position":{"x":80,"y":1430},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "width":1080,"height":180,"fill":"#1e3a8a","cornerRadius":20},
          {"id":"cta-t","kind":"text","position":{"x":140,"y":1470},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"Speak to our pharmacist — no appointment","fontSize":36,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":960,"align":"left"},
          {"id":"cta-s","kind":"text","position":{"x":140,"y":1540},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":5,
           "text":"{{phone}}  •  {{address}}","fontSize":22,
           "fontFamily":"Inter, sans-serif","fill":"#bfdbfe","width":960,"align":"left"},

          {"id":"footer","kind":"text","position":{"x":80,"y":1690},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":6,
           "text":"Always read the patient information leaflet. This advice is for parents/guardians — it is not a substitute for medical assessment.",
           "fontSize":18,"fontFamily":"Inter, sans-serif","fill":"#475569","width":1080,"align":"left"}
        ]
        """);

    private static string Canvas(string name, string background, string shapes)
    {
        const int Width = 1240;
        const int Height = 1754;
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("N");
        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "ownerId": "system",
          "name": "{{name}}",
          "width": {{Width}},
          "height": {{Height}},
          "background": "{{background}}",
          "viewport": {"pan":{"x":0,"y":0},"zoom":1},
          "shapes": {{shapes}},
          "createdAtUtc": "{{now}}",
          "updatedAtUtc": "{{now}}"
        }
        """;
    }
}
