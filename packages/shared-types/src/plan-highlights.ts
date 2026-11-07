import type { SubscriptionTier } from "./subscription";

/**
 * Each plan's selling points, for pages that cannot ask the API: the
 * marketing pricing teaser (static, must render with the API down) and the
 * onboarding plan picker.
 *
 * <b>A copy, checked.</b> The source is `PlanCopy.Highlights` in
 * apps/api/src/Acme.Application/Subscriptions/Plans.cs, which writes these
 * lines from the limits the API enforces. `PlanCopyTests` reads this file and
 * fails if a line here differs from what the API would say — so a limit
 * cannot change on one side only, and nothing can be advertised that the
 * product does not do. Keep the format: one array per tier, plain strings.
 */
export const PLAN_HIGHLIGHTS: Record<SubscriptionTier, readonly string[]> = {
  Free: [
    "Up to 5 saved posters",
    "3 AI poster designs a month",
    "Up to 50 patient contacts",
    "Watermark on exports",
  ],
  Starter: [
    "Up to 30 saved posters",
    "50 AI poster designs a month",
    "5 marketing videos a month",
    "1 in-pharmacy screen",
    "Email campaigns to 500 patients a month",
    "Up to 500 patient contacts",
    "No watermark",
  ],
  Pro: [
    "Unlimited posters",
    "Unlimited AI poster designs",
    "30 marketing videos a month, including AI imagery",
    "Unlimited in-pharmacy screens",
    "Email campaigns to 5,000 patients a month",
    "Unlimited patient contacts",
    "No watermark",
    "Analytics dashboard",
  ],
};

/**
 * Monthly prices in pounds, for the same pages and for the home page's
 * structured data (search results can show them). Checked against
 * `MonthlyPriceGbp` in Plans.cs by `PlanCopyTests`, like the lines above.
 */
export const PLAN_PRICES_GBP: Record<SubscriptionTier, number> = {
  Free: 0,
  Starter: 7,
  Pro: 25,
};
