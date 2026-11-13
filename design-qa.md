# Shared sidebar design QA — 2026-11-12

final result: passed

Scope: the shared sidebar on every app page. Latest owner revision: filled, consistent and clearer destination icons; slightly larger and heavier labels, retaining the earlier increased spacing, white active/hover state and original white rounded open-submenu container. This revision supersedes the earlier request's blue rectangular selection. The owner's explicit requirements are to follow the reference's item styling, retain the existing teal background, and remove the logo header. Existing route groups and permissions remain functional.

## Evidence and normalization

- Source: `docs/inventory-reference/source.jpg`, 872 × 778 photographic pixels; unwarped CSS dimensions and density unknown.
- Browser desktop: `docs/sidebar-reference/desktop.png`, 1600 × 900 CSS pixels at density 1.
- Sidebar-only capture: `docs/sidebar-reference/sidebar.png`, 260 × 900 pixels.
- Browser mobile: `docs/sidebar-reference/mobile.png`, 390 × 844 CSS pixels at density 1, captured after the drawer's transition completed.
- Combined comparison: `docs/sidebar-reference/comparison.png`. Source and implementation were viewed together in that composite; the source's small photographic sidebar was also inspected against the full-resolution sidebar-only capture. No density-equivalent or exact photographic pixel-diff claim is made.
- Previous inventory QA preserved in `docs/inventory-reference/design-qa.md`; previous doctor QA preserved in `docs/xray/design-qa-2026-11-03.md`.

## Required surfaces

- Typography: existing Persian/Latin font stack retained; 17px main labels at weight 500 and 15px nested labels. No brand title or section headings occupy the top. Text alignment remains RTL as required by the system.
- Spacing: first row starts 24px from the top, 60px main rows with 12px separation, 14px icon/text gap and 25px white-card corners; 52px nested rows. Existing 260px expanded and 80px collapsed widths are preserved. A small footer retains the collapse/pin function formerly in the removed header.
- Colours: unchanged teal gradient; white labels/icons; white active row and white active hover with teal text. Open groups restore the original white rounded container, grey nested labels and a pale-teal selected child, as explicitly requested. Source navy is intentionally not copied because the owner explicitly asked to retain teal.
- Icons/assets: existing Remix Icon filled variants, 24px main / 22px nested. Every requested variant exists in the local icon font. No emoji, handmade SVG, raster logo or substitute brand header is used.
- Copy/content: existing hospital destinations and authorized submenus retained; no fictional electronics-related menu entries are added. Hidden-menu and permission filtering are preserved.

## Findings and fixes

The original small sizes, compact gaps and blue selection were revised to the owner’s requested larger spacing and white card states. No actionable P0/P1/P2 visual issue remains within this requested scope. The expanded white submenu was also inspected in `docs/sidebar-reference/submenu.png`. A mobile screenshot originally captured the drawer mid-animation; the test now waits until its right edge reaches the viewport edge and the final screenshot shows the fully open drawer. This was a capture timing issue, not a final layout mismatch.

## Validation

- Production Vue/TypeScript/Vite build passed.
- The preceding combined suite of 23 Chromium cases passed across inventory and shared sidebar scenarios: collapse, pin, hover and keyboard expansion, synchronized layout width, authorized routes, hidden destinations, group navigation and white active/hover states, white open-group container, filled icons, no brand header, larger row geometry and 17px/500 main-label typography, touch desktop, mobile opening/closing, plus existing inventory regression checks.
- After the latest filled-icon and typography revision, all 20 sidebar Chromium cases passed again, including mobile, keyboard, permissions, active/hover and open-submenu states. The new desktop, mobile and open-submenu captures were visually inspected.
- Desktop and mobile final captures were visually inspected. Browser tests use mocked API data; no live hospital records were written. Earlier inventory scenarios also check browser page errors.
- `git diff --check` passed.

Exact source-photo font identity and camera pixels are not recoverable; this pass is for the owner's revised shared-sidebar design, not an identical reproduction of the navy English photograph.
