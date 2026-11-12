# Inventory list design QA — 2026-11-12

final result: passed

Scope: the inventory list and warehouse stock list, incorporating the owner's latest corrections. This result confirms the revised UI and tested interactions; it does not certify pixel identity with the original perspective photograph.

## Evidence

- Source: `docs/inventory-reference/source.jpg`, 872 × 778 photographed pixels; original CSS viewport/density unknown.
- Final desktop: `docs/inventory-reference/desktop.png`, 1440 × 1000 CSS viewport, deviceScaleFactor 1.
- Final mobile: `docs/inventory-reference/mobile.png`, 390 × 844 CSS viewport, full-page capture, deviceScaleFactor 1.
- Tablet: `docs/inventory-reference/tablet.png`, 1024 × 768 CSS viewport, full-page capture.
- Normalized list: `docs/inventory-reference/table-normalized.png`, list element captured at a 1180 × 900 CSS viewport.
- Full and detailed combined comparisons: `docs/inventory-reference/comparison-full.png` and `comparison-detail.png`. Source and implementation were opened in the same combined comparison. The source was not stretched into a supposed exact CSS viewport; perspective and unknown density are explicitly acknowledged.
- Detailed analysis, estimates and data rules: `docs/inventory-reference/ANALYSIS.md`.

## Priority of the latest owner instructions

Preserve the original sidebar colours and structure. Put tabs, Register Product and Register Purchase above the list. Remove the displayed SKU column. Put each row's action menu in the final column. Increase product icons and stock numbers. These explicit instructions supersede the corresponding parts of the original photo.

## Comparison history and fixes

1. [P1, resolved] Initial photo-derived navy sidebar exceeded the desired scope. Restored AppLayout.vue exactly to its repository version. Browser checks verify the original teal gradient.
2. [P1, resolved] Initial tab/action strip appeared below the list. Restored its position above the list, renamed the create action «ثبت محصول», and checked control/table bounding boxes.
3. [P1, resolved] SKU and first-cell action menu no longer matched the owner's revision. Removed visible SKU; retained record codes for search and editing. Moved the three-dot menu into a final actions cell, visible only to authorized users.
4. [P2, resolved] Small silhouettes and quantities were difficult to read. Product icons are 26px; stock numbers are 16px, tabular Latin numerals; the quantity column has more space and numbers do not wrap.
5. [P2, resolved] Hidden absolutely positioned table text contributed to document overflow on mobile. Positioned the horizontal table-scroll region to contain it. The browser test now confirms document width stays within 390px while the table scrolls independently.
6. [P2, resolved] Initial muted badge text had insufficient contrast for small text. Updated green/orange/red foregrounds to approximately 4.87/4.67/5.07 contrast against their backgrounds, retaining soft coloured tiles and explicit text labels.

## Required fidelity surfaces

- Typography: existing Vazirmatn for Persian, locally loaded Poppins for numbers; 20px title, 14px table content, 13px header, 16px quantities. Persian content and RTL direction intentionally follow the system and owner context.
- Spacing/layout: compact top toolbar, six cells on the authorized main list (five data fields plus operations), five on warehouse details, 64px row minimum, eight records per page by default. No whole-page horizontal overflow on mobile. Focus and action controls remain accessible.
- Colour: original app sidebar and shell restored. White list, very light header/row rules, green/orange/pink stock labels. Colour is accompanied by text.
- Assets/icons: real Remix Icon glyphs for capsules, drops, injections, bottles and product kinds; no emoji, handmade illustrations or placeholder pictures. Real products do not acquire the photographed electronics' thumbnails. Exact source icon files cannot be recovered from the photograph.
- Copy/content: real product records, quantities, thresholds and stock locations feed the UI. Test fixtures are synthetic and do not insert database records. No fictional shelf IDs are added. SKU is absent from the displayed columns as requested.

## Verification

Production build passed (`npm --prefix frontend run build`). Three Chromium tests passed: catalogue pagination beyond 100 products, query/filter/pagination states, number formatting and status logic; top toolbar, original sidebar, last-cell actions, edit modal, exact 26px/16px sizing; mobile empty state and document width; warehouse details/history. Browser page errors were checked and none were reported by these scenarios. Tests mock API responses; live hospital database connectivity and financial write workflows were not retested because this change modifies frontend presentation only.

No remaining actionable P0/P1/P2 issue was found within this revised scope. Original photograph fonts, device density and unwarped CSS dimensions remain unknown; exact photographic pixel equality is not asserted.
