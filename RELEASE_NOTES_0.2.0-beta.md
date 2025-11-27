# Koware v0.2.0-beta — Release Notes

## Highlights
- Installer now ships as a single-file EXE for easy distribution.
- Returning users see Re-install or Remove options.
- Search results ranked by similarity and visually highlighted.
- Interactive selection supports “c to cancel”.

## New
- **Installer**
  - Single-file, self-contained EXE output.
  - Copies final EXE to Desktop and repo root via script switches.
  - Detects existing install and shows:
    - Re-install
    - Remove (uninstalls and removes PATH entry)
  - Displays installed path and version.
- **CLI**
  - Similarity-ranked results for search/stream/watch.
  - Best match highlighted in bright yellow; others in darker yellow.
  - Interactive prompt: add “c to cancel”.

## Packaging
- `Scripts/publish-installer.ps1`:
  - Default self-contained, single-file publish.
  - Optional copying of distributable EXE to convenient locations.

## Fixes/Compatibility
- No breaking CLI syntax changes.
- Maintains compatibility with older PowerShell.

## Upgrade Notes
- To install: run the single EXE and accept the usage notice.
- To update: run the installer again → choose Re-install.
- To remove: run the installer → Remove.

## Commands
- Search: `koware search "title"`
- Watch: `koware watch "title"` (Enter = best match, `c` = cancel)
- Doctor: `koware doctor`
- Provider: `koware provider --enable/--disable <name>`

## Version
- 0.2.0-beta
