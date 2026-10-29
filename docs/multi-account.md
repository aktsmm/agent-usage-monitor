# Multi-account support

Several accounts of the same service (for example two GitHub Copilot logins, or two Codex / Claude contracts on one PC) are shown **side by side and never summed** — each is a separate contract.

## Model

- The existing single-account settings remain the **primary** account of each service (ids `primary-copilot`, `primary-codex`, `primary-claude`). Old `settings.json` files load unchanged.
- Extra accounts are `ExtraAccounts` (an `AccountProfile` each). Display choices are `AccountPrefs[profileId]`: `Alias`, `Visible`, `ShowInStrip`.
- Name shown = alias, otherwise the identity before `@` (or the Codex home folder name). Same names within one service get `#1`, `#2`.
- Strip: only accounts that are visible **and** `ShowInStrip`. The primary account defaults to shown, extras to hidden (the "pinned one" default). One item per service keeps the text `Copilot 42%`; with several it becomes `Copilot·alias 42%`.
- Each extra is polled on its own. A failure or `AccountMismatch` clears only that account's cached values. History and notifications are already keyed by the account id.
- Aliases are stored locally in `settings.json` only and are never sent anywhere or written to logs.

## How accounts are switched on one PC

| Service | Supported setup | Status |
| --- | --- | --- |
| Codex | separate `CODEX_HOME` folders per account | engine ready, needs a real-machine check |
| Claude Code | separate `CLAUDE_CONFIG_DIR` / statusline snapshot per account, with a manual label | the snapshot cannot be bound to an account automatically (see README) |
| Copilot | one profile per GitHub login (`Login`, optional credential target) | reading a token of a non-active `gh` login is unverified |

## Not done yet

- Settings window (add / remove / alias / show / strip toggle): mock in `docs/ui/multi-account-mock.html`, waiting for approval.
- Real-machine spikes for the three rows above.
- Per-account rows in the tray details view.
