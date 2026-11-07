"""
Canvas constants shared by the validator, and the legacy photo allow-list.

The design system itself — palette, type, archetypes, photo slots — now lives
in `house_style.py`, drawn by the same builders as the built-in house
templates. The palettes and free-form archetypes that used to be here belonged
to the old designer, which asked the model to draw raw canvas JSON; they are
gone with it so nothing can quietly go on using them.

`APPROVED_PHOTOS` stays for one reason: `validate_canvas` still accepts the
hand-written gallery templates from before the house set, three of which use
these Unsplash photographs. No new design references them — generated posters
use photographs stored in our own blob storage, never a hot-linked URL.
"""

from __future__ import annotations

# A4 portrait at 150 DPI - matches packages/shared-types/src/page-size.ts and
# every seeded template.
CANVAS_WIDTH = 1240
CANVAS_HEIGHT = 1754

# Keep type clear of the trim edge.
SAFE_MARGIN = 90

FONT_FAMILY = "Inter, sans-serif"

# Legacy: referenced by older gallery templates only. See the module docstring.
APPROVED_PHOTOS: dict[str, str] = {
    "pharmacist": "https://images.unsplash.com/photo-1559757175-5700dde675bc?w=1400&q=80&auto=format&fit=crop",
    "clinical": "https://images.unsplash.com/photo-1576091160550-2173dba999ef?w=1400&q=80&auto=format&fit=crop",
    "vaccine": "https://images.unsplash.com/photo-1612277795421-9bc7706a4a34?w=1400&q=80&auto=format&fit=crop",
}
