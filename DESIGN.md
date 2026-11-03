# XRay doctor list: source analysis and implementation

Source: https://templates.iqonic.design/xray/html/doctor-list.html
Captured: 2026-11-03. Target: existing Vue 3 / Bootstrap 5 / RTL hospital application.

## Evidence

- [Desktop capture](docs/xray/doctor-list-source.png), 1920 × 1080. This capture shows the header and first two rows; it does not establish the entire desktop page visually.
- [Mobile capture](docs/xray/doctor-list-mobile-source.png), requested 390 × 844 viewport, full-page output. The list becomes one column; navigation collapses.
- Live HTML: sixteen cards in the same repeated hierarchy, with seven recurring avatar assets (`images/user/12.jpg` through `18.jpg`).
- Styles inspected: source `css/typography.css`, `css/style.css`, `css/responsive.css`; source HTML uses Bootstrap `col-sm-6 col-md-3`.

## Structure and behaviour

The shared XRay shell consists of a teal navigation rail, a pale content background, a rounded white top bar and a white title strip. The doctor-list content has no search field, filters, statistics, add button or pagination. The search field in the source belongs to the shell. Each doctor card contains, in order:

1. An 80px circular illustration.
2. The doctor's name, specialty, and website.
3. A short biography.
4. Three square social links: Facebook, Twitter, Google.
5. A primary `View Profile` link.

The desktop grid is four columns, changes to two from 576px through 767px, and one below 576px. Those breakpoints follow the source Bootstrap classes; they are not inferred from the mobile capture alone. Cards are repeated with natural content height, 20px padding and a 30px bottom margin. No invented dashboard widgets were added.

## Extracted tokens

| Role | Source value | Evidence |
| --- | --- | --- |
| Primary | `#089bab` | primary button and social-link CSS |
| Gradient end | `#0db5c8` | `.btn-primary` |
| Social tile | `#ceebee` | `.iq-doc-social-info ul li a` |
| Content background | `#eff7f8` | branding and source typography CSS |
| Heading | `#3f414d`, weight 400 | heading CSS |
| Secondary text | `#a09e9e` | source body typography |
| Body | Poppins, 14px, 25px line height | typography CSS |
| Doctor name | `1.4em` (19.6px at 14px) | `h4` |
| Card radius | 25px | `.iq-card` |
| Card shadow | `0 4px 5px 1px rgba(175,175,175,.1)` | `.iq-card` |
| Card padding | 20px | `.iq-card-body` |
| Title strip | minimum 60px, horizontal padding 20px | `.iq-card-header` |
| Social link | 30 × 30px, 5px radius, horizontal margin 3px | doctor-social CSS |
| Avatar | 80 × 80px, circular | `avatar-80`, source HTML |

Source spacing `mt-3`, `mt-2`, `mb-3` maps to 16px / 8px / 16px. The implementation uses a 30px grid gutter, following Bootstrap 4's original layout conventions; exact gutter equality against the current live build is not visually verified.

## Integration decisions and explicit differences

`frontend/src/features/users/DoctorsPage.vue` adds `/doctors`, preserving `/doctor` as the clinical work queue. It reuses the existing RTL shell, Persian fonts and translated labels. Consequently the whole page is not an identical English/LTR screenshot.

The cards read real staff records, filtered server-side by `isDoctor=true`, including all result pages. No sample doctors or fabricated medical biographies are inserted. Missing fields show an explicit empty value; missing social links are inert tiles rather than misleading links. Profile details open in an accessible existing modal, rather than navigating to the template's fictional patient/account profile.

Specialty, biography, website, social links and avatar choice are persisted and edited through **Management → Staff**. The seven illustrations are copied into `frontend/public/images/doctors`; fonts and Remix Icon are existing local dependencies. No remote template assets are required at runtime. These illustrations are template artwork, not photographs of actual staff. Their use is subject to the template owner's license.

## UX findings relevant to this system

The reference is a display directory, not a staff-management workflow. Placing clinical queue operations or staff-management forms on the cards would change its composition. Management remains in the existing staff screen. Long Persian names and real biographies can produce taller cards than the sample English text; wrapping is permitted rather than truncating medical credentials. Every profile control is a real button, links are keyboard accessible, and empty social destinations do not navigate to `#`. Loading, failure and empty states are explicit.

## Remaining verification

Source screenshots, HTML and styles were inspected; frontend compilation and backend integration checks are recorded in `docs/implementation-checks.md`. There is no available interactive browser tool in this session. Local rendered screenshots, hover-state comparison, keyboard interaction in the rendered app and pixel-level desktop/mobile comparison remain unverified. No claim of 100% visual equality is made.
