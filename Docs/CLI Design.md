# To watch anime

koware watch <anime name> <episode number>

# To download anime

koware download <anime name> <episode number>

# To continue watching 

## The last watched episode

koware last 

## To continue from a certain episode

koware continue <anime name> <episode number>




# Koware CLI Reference

This document describes the intended final CLI surface of `koware`.

## Global syntax

`koware <command> [options] [arguments]`

## Global options

- `-h`, `--help`  
  Show help for a command.
- `--version`  
  Show the installed koware version.
- `--provider <name>`  
  Anime catalog provider to use. Default: `allanime`.
- `--log-level <trace|debug|info|warn|error>`  
  Override logging level for the current invocation.

> Note: Provider-specific options (e.g. sub/dub) are documented under each command.

## Commands

### watch

**Usage**

`koware watch <anime name> [--episode <number>] [--quality <label>] [--index <n>] [--sub|--dub] [--non-interactive]`

**Behavior**

- Searches for `<anime name>` using the configured provider.
- If multiple shows match:
  - In interactive mode (default), shows a numbered list and prompts for selection.
  - In `--non-interactive` mode, automatically picks the first result.
- Resolves episodes for the selected show.
- If `--episode` is omitted, picks the first available episode.
- Resolves stream URLs, applies `--quality` if provided, and launches the configured player.

**Examples**

- `koware watch "Fullmetal Alchemist: Brotherhood"`
- `koware watch "Bleach" --episode 50 --quality 1080p`
- `koware watch "One Piece" --index 2`

### download

**Usage**

`koware download <anime name> [--episode <number> | --range <start>-<end>] [--quality <label>] [--output <dir>] [--filename-template <template>] [--sub|--dub]`

**Behavior**

- Same search and selection flow as `watch`.
- For a single `--episode`, downloads that episode.
- For `--range`, downloads all episodes in the inclusive range.
- Writes files to `--output` (default: current directory).
- Uses `--filename-template` for naming (e.g. `"{title}.E{episode}.{quality}.mp4"`).

**Examples**

- `koware download "Jujutsu Kaisen" --episode 1`
- `koware download "Jujutsu Kaisen" --range 1-12 --quality 720p --output "./jujutsu_s1"`

### last

**Usage**

`koware last [--play] [--json]`

**Behavior**

- Reads history and prints the last watched entry:
  - anime title
  - provider
  - last watched episode number
  - timestamp
- With `--play`, immediately replays that episode using the same quality/provider.
- With `--json`, prints a machine-readable JSON representation instead of a humanized summary.

**Examples**

- `koware last`
- `koware last --play`
- `koware last --json`

### continue

**Usage**

Preferred flexible form:

`koware continue [<anime name>] [--from <episode>] [--quality <label>] [--sub|--dub]`

Simple positional form (as in the quick reference above):

`koware continue <anime name> <episode number>`

**Behavior**

- If `<anime name>` is omitted:
  - Uses the last watched anime from history.
  - Continues from the next episode after the last watched one, unless `--from` is provided.
- If `<anime name>` is provided:
  - Looks up history for that anime and continues from the next episode.
  - If there is no history, behaves like `watch` with `--episode` or `--from`.
- Launches the player with the selected episode and `--quality` preference.

**Examples**

- `koware continue` (continue the globally last watched anime)
- `koware continue "Bleach"` (continue Bleach from the next unwatched episode)
- `koware continue "Bleach" --from 50 --quality 1080p`

### search

**Usage**

`koware search <anime name> [--provider <name>] [--json]`

**Behavior**

- Searches for matching anime and prints a numbered list of candidates.
- With `--json`, outputs detailed information suitable for scripting.

**Examples**

- `koware search "Vinland Saga"`
- `koware search "Naruto" --json`

### episodes

**Usage**

`koware episodes <anime name> [--provider <name>] [--json]`

**Behavior**

- Resolves the anime, then lists all known episodes (number and title).
- With `--json`, outputs a JSON list of episodes.

**Examples**

- `koware episodes "Spy x Family"`
- `koware episodes "Spy x Family" --json`

### config

**Usage**

`koware config [--json]`

**Behavior**

- Prints the effective configuration:
  - provider defaults (sub/dub, search limit)
  - player command/args
  - history file location, etc.
- With `--json`, prints raw configuration.

---

## Exit codes

- `0` – Success.
- `1` – Usage error, validation error, or recoverable failure (no matches, no streams, player failed to start).
- `2` – Operation canceled by user (Ctrl+C).
- `>2` – Reserved for future use.
