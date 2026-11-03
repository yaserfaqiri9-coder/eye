# Implementation and verification

## Where to use the features

- **مدیریت → مدیریت بخش‌ها** (`/departments`): administrators with `settings.edit` create active departments or reactivate existing ones. Inactive departments remain in historical records and disappear from new reception selections. Department codes are immutable because clinical workflows use the built-in codes.
- **مدیریت → تأمین‌کنندگان** (`/suppliers`): inventory users with `inventory.create` create/edit suppliers, phone and address, or deactivate them. Supplier selection on purchases uses active suppliers and requires a selection in the UI.
- **مدیریت → فهرست داکتران** (`/doctors`): real doctor profiles in the XRay card layout. Create/edit these through **کارمندان**, checking **داکتر است**.

## Data and upgrade

The EF migration `DirectoriesAndDoctorProfiles` adds a Suppliers table, nullable Purchase.SupplierId foreign key, doctor profile fields and concurrency versions on departments. It does not delete existing purchases, staff or departments. Existing purchase names remain intact. For backwards compatibility, legacy API clients may still submit SupplierName without SupplierId; the current purchase UI submits SupplierId. With a supplied ID, the API validates an active supplier and resolves the name from the database, ignoring any client-supplied name. Purchase.SupplierName remains a historical snapshot after supplier renaming.

Department and supplier updates require a version; stale writes return a conflict. Unique codes/names and permission checks are enforced on the API. HTTP/HTTPS profile links are validated, and browser links use noopener/noreferrer. Lookup caches are cleared after staff/department changes.

Install on a hospital host through the existing `deploy/windows/update.ps1` process, which backs up and applies migrations. No live hospital database was connected or updated during this work.

## Checks

- Frontend: `npm --prefix frontend run build` passed (Vue/TypeScript checking and production Vite build).
- Backend solution build passed. Two pre-existing test warnings remain: EF1002 in MigrationSafetyTests and xUnit1031 in ConcurrencyTests.
- Three new DirectoryTests passed against disposable MySQL 8.4: department permissions/lookup activation/stale update/duplicate code, supplier selection/idempotency/historical name/inactive and unknown IDs, and doctor filtering/profile-link validation.
- 26 existing InventoryTests, VisitFlowTests, ReceptionTests and MigrationSafetyTests passed. The migration rehearsal upgraded an older populated disposable schema and verified existing records survived.
- `git diff --check` passed.
- Visual QA remains blocked as detailed in ../design-qa.md. No browser-rendered interaction or pixel comparison is claimed.

## Audio station follow-up

The audio station is kept alive when navigating within the application, while a reload still requires a user click to unlock browser audio. Desk status now refreshes every 15 seconds and distinguishes request failures from transient queue-load failures. Offline messages explain activation and link authorized users to the station. The existing audio E2E scenario now includes navigation and offline-notice recovery checks; that browser scenario has not been executed in this session. The production frontend build passed after these changes. Real speaker output and installed voice clips on the hospital host remain unverified.
