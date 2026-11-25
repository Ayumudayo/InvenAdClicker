# Repository Instructions (`InvenAdClicker`)

## Overview
- `InvenAdClicker` is a lightweight Rust HTTP-only Windows CLI (`InvenAdClicker.exe`).
- Do **not** reintroduce browser/Playwright dependencies or embed full JavaScript engine crates (`boa`, `quickjs`, etc.); `src/adscript/` intentionally evaluates only the minimal subset required for Inven ad slots.

## Git & Release Workflow
- `main` is branch-protected (`MyDefaultSet`): never push directly to `main`; always open a Pull Request.
- Use Conventional Commits (`feat:`, `fix:`, `perf:`, `refactor:`, `chore:`, `ci:`, `feat!:`) and prefer **Squash and merge** with a clean PR title so SemVer classification is unambiguous.
- Versioning, `Cargo.toml` / `Cargo.lock` bumps, `CHANGELOG.md`, tags, and GitHub Releases are managed automatically by `release-please` (`release-please-config.json`, `.release-please-manifest.json`, `.github/workflows/release.yml`):
  - `fix:` → Patch, `feat:` → Minor, `feat!:` / `BREAKING CHANGE:` → Major, `chore:` / `ci:` / `docs:` → no release.
  - Do not manually edit `Cargo.toml` version or `CHANGELOG.md` in routine PRs.
  - Only add a `Release-As: X.Y.Z` footer when intentionally overriding the SemVer bump.

## Build & Verification
Run checks from the repository root before committing:
- `cargo clippy --all-targets --locked -- -W clippy::all` (must pass with 0 warnings)
- `cargo test --release --locked`
- `cargo build --release --locked` (outputs `target/release/InvenAdClicker.exe`)
- Release packaging (`.github/workflows/release.yml` and `build.ps1 -Package`) must include **only** `InvenAdClicker.exe`, `config.json.example`, `README.md`, and `LICENSE`.

## Safety & Testing Constraints
- **Never** trigger live ad clicks (`click_lx`) without explicit user permission; verify changes with unit tests, `--collect-only`, or `--inspect-placements`.
- **Never** modify or commit user runtime files (`config.json`, `credentials.dat`, `logs/`).
