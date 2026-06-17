using Acme.Application.Templates;
using Acme.Domain.Templates;

namespace Acme.Infrastructure.Persistence;

public sealed class TemplateSeeder(ITemplateRepository repo) : ITemplateSeeder
{
    public async Task SeedIfEmptyAsync(CancellationToken ct)
    {
        foreach (var t in DefaultTemplates())
        {
            var existing = await repo.FindByIdAsync(t.Id, ct);
            if (existing is null)
                await repo.AddAsync(t, ct);
            else
                await repo.UpdateAsync(t, ct);
        }
    }

    private static IEnumerable<Template> DefaultTemplates()
    {
        yield return Template.CreateWithId(
            "sys-flu-vaccination",
            "Flu Vaccination Reminder",
            "Bold A4 reminder that your pharmacy is offering flu shots.",
            TemplateCategory.Vaccination,
            new[] { "flu", "vaccine", "reminder", "seasonal" },
            FluVaccinationCanvas());

        yield return Template.CreateWithId(
            "sys-vitamin-d",
            "Vitamin D Awareness",
            "Educational poster on the signs of Vitamin D deficiency.",
            TemplateCategory.Awareness,
            new[] { "vitamin", "wellness", "education" },
            VitaminDCanvas());

        yield return Template.CreateWithId(
            "sys-loyalty-rewards",
            "Loyalty Rewards Promo",
            "High-impact dark theme announcing a loyalty program.",
            TemplateCategory.Promotion,
            new[] { "loyalty", "promo", "rewards" },
            LoyaltyRewardsCanvas());

        yield return Template.CreateWithId(
            "sys-hand-hygiene",
            "Hand Hygiene Steps",
            "Step-by-step poster on the 20-second handwashing technique.",
            TemplateCategory.Safety,
            new[] { "hygiene", "safety", "infection-control" },
            HandHygieneCanvas());

        yield return Template.CreateWithId(
            "sys-allergy-season",
            "Spring Allergy Survival Kit",
            "Seasonal poster grouping antihistamines, sprays, and eye drops.",
            TemplateCategory.Seasonal,
            new[] { "allergy", "spring", "tips" },
            AllergySeasonCanvas());

        yield return Template.CreateWithId(
            "sys-welcome",
            "New Patient Welcome",
            "Friendly welcome poster with three service highlights.",
            TemplateCategory.General,
            new[] { "welcome", "intro", "services" },
            WelcomeCanvas());
    }

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
           "text":"555-0102  •  acmepharmacy.com","fontSize":28,
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
           "text":"Welcome to Acme Pharmacy","fontSize":52,
           "fontFamily":"Inter, sans-serif","fill":"#ffffff","width":1080,"align":"left"},
          {"id":"hdr-2","kind":"text","position":{"x":80,"y":120},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":2,
           "text":"Your community pharmacy since 1987","fontSize":24,
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
           "text":"123 Main Street, Anytown","fontSize":30,
           "fontFamily":"Inter, sans-serif","fill":"#cbd5e1","width":960,"align":"left"},
          {"id":"contact-3","kind":"text","position":{"x":140,"y":1290},"rotation":0,
           "scale":{"x":1,"y":1},"opacity":1,"draggable":true,"zIndex":4,
           "text":"555-0123  •  acmepharmacy.com","fontSize":30,
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
