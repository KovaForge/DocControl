# Changelog

This changelog was generated from the repository's git history and grouped into human-readable milestones. The project does not currently use git tags or formal release notes, so entries below are date-based summaries rather than strict versioned releases.

## 2026-06-02

### Document deletion and maintenance fixes

- Added support for deleting a single document through the API and CLI tooling.
- Fixed the document purge function signature and corrected contributor role usage so cleanup operations behave consistently.

## 2026-06-01

### Filename rule correction

- Rolled back a filename change around free-text handling to keep document naming aligned with the existing rules.

## 2026-05-01

### OpenClaw and CLI integration

- Added OpenClaw integration documentation and a CLI manifest for external tooling.
- Introduced direct DocControl CLI integration so projects and documents can be queried without relying on the web UI.
- Added Microsoft device-code login support for CLI usage.
- Adjusted auth token handling so CLI and automation scenarios are more durable.
- Marked Microsoft CLI tokens as MFA-satisfied after successful validation.
- Added standalone level-code management support.
- Reviewed and tightened backup-code UX during the auth flow.

## 2026-04-08

### Filename normalization

- Normalized generated filenames so spaces in the free-text portion are converted to underscores.

## 2025-12-26

### UI and account flow polish

- Refactored navigation into a shared configuration.
- Updated the authentication screens with a new layout and background treatment.
- Added MFA backup code support in the UI.
- Removed legacy account-linking options from settings once the migration path had stabilized.

## 2025-12-25

### Auth hardening and profile improvements

- Added a dedicated `/login` route and cleaned up the authentication flow.
- Introduced a profile page for account settings.
- Added persistent auth tokens and a profile update endpoint.
- Hardened invite handling, auth error behavior, and AI-related logging.
- Encrypted MFA secrets at rest.
- Fixed Azure Static Web Apps auth issues including redirect loops, anonymous auth endpoint access, and shadowed auth-context variables.
- Added legacy-account migration support for Microsoft login, along with a controlled path to skip or preserve legacy auth details when needed.
- Bumped the application version to `1.1.0`.

## 2025-12-24

### Major feature expansion

- Expanded project configuration to support up to six code and document levels, including separators and padding rules.
- Added project update functionality and richer project properties.
- Improved code import so hierarchical structures, quoted CSV fields, descriptions, counts, and validation are handled more reliably.
- Added JSON and CSV import/export support for codes and documents.
- Moved import/export workflows into the management area and improved feedback during long-running actions.
- Added document preview support before allocation.
- Added pagination and total counts for document listing.
- Added sorting improvements across document, code, and series tables.
- Added code-series management features, including purge support.
- Added pending invite management, invite-token handling, and clearer project/member UI states.
- Added AI configuration visibility in settings and tightened recommendation/input validation.
- Added support for per-user password auth and encrypted API key storage.
- Improved document generation and code recommendation logic to better use level-based catalogs.

## 2025-12-23

### Initial product launch

- Bootstrapped the backend on Azure Functions isolated with PostgreSQL support.
- Bootstrapped the frontend with React, TypeScript, and Vite.
- Added project management APIs and frontend views.
- Added member management and invite acceptance.
- Added user registration, login, and TOTP MFA setup, including QR code display.
- Added document generation, document import, and document purge capabilities.
- Added code catalog import and hierarchical code-building support.
- Added project settings, audit access, and AI helper endpoints.
- Added deployment and build automation for Azure Static Web Apps.
- Added initial setup and usage documentation.

## Notes

- This file is intended to be readable by humans first. It summarizes notable changes and intentionally omits many small refactors, merge commits, and workflow-only tweaks.
- If you want stricter release tracking later, the next sensible step is to introduce git tags or a versioning policy so future changelog entries can map cleanly to releases.
