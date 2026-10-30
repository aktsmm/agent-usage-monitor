# Multi-account support

Several accounts of the same service (for example two GitHub Copilot logins, or two Codex / Claude contracts on one PC) are shown **side by side and never summed** — each is a separate contract. Available since v0.2.6.

## Model

- The existing single-account settings remain the **primary** account of each service (ids `primary-copilot`, `primary-codex`, `primary-claude`). Old `settings.json` files load unchanged.
- Extra accounts are `ExtraAccounts` (an `AccountProfile` each). Display choices are `AccountPrefs[profileId]`: `Alias`, `Visible`, `ShowInStrip`.
- Name shown = alias, otherwise the identity before `@` (or the Codex home folder name). Same names within one service get `#1`, `#2`.
- Strip: only accounts that are visible **and** `ShowInStrip`. The primary account defaults to shown, extras to hidden. One item per service keeps the text `Copilot 42%`; with several it becomes `Copilot·alias 42%`.
- Each extra is polled on its own. Aliases are stored locally in `settings.json` only and are never sent anywhere or written to logs.

## One account never changes what another shows

Every rule that used to assume "one account per service" is decided per account (`AccountOverview`, `QuotaVisibility`):

- A failure or `AccountMismatch` of the primary account hides only the primary account's values and marks only the primary as failed (overview and strip). Extras keep their own state.
- Unlimited buckets are hidden only inside an account that also has limited ones. An account that reports only unlimited buckets (for example a business plan) stays visible as one card.
- An account that has no value yet is listed with a waiting message instead of silently disappearing.
- Startup never pins the primary account from values that belong to an additional account.
- Notices and the history account picker name the account when a service has several.
- The same account cannot be added twice: Copilot profiles are compared by the verified GitHub account ID (login names can change), others by identity. A settings file that lists the same account twice is rejected on load and save.

## How accounts are switched on one PC

| Service | Supported setup | Status |
| --- | --- | --- |
| Copilot | one profile per GitHub CLI (`gh`) login | verified on a real machine with three logins: a non-active login returns its own usage and an all-unlimited business account is shown |
| Codex | separate `CODEX_HOME` folders per account | the default home works; a second home is not configured or verified |
| Claude Code | separate `CLAUDE_CONFIG_DIR` / statusline snapshot per account, with a manual label | the configured snapshot works; additional config directories are unverified, and snapshots cannot be bound to accounts automatically (see README) |

## Automatic discovery

For Copilot, the Add Account dialog reads `gh auth status --hostname github.com --json hosts` only when the user asks to find accounts. It groups duplicate logins, offers only logins whose status is successful, and does not switch the active login. The profile is saved only when the user saves Settings. The dialog states these conditions on screen and explains an empty result.

Codex and Claude are never detected automatically: paths and labels are entered by hand. Accounts that exist only inside the GitHub Copilot desktop app (not signed in to `gh`) cannot be detected: the app's own credentials are not read. Sign that account in with `gh auth login` and search again.

`gh auth login` makes the new login the active one. Saved profiles are pinned to the verified GitHub account ID, so the displayed accounts do not change.

## VS Code bridge (not shipped)

`integrations/vscode` contains an optional extension that would share quota snapshots from VS Code's own GitHub sign-in. It is not reachable from the app UI and is not part of any release: it is unverified against a real VS Code session and depends on the private usage endpoint. The source stays for a future decision; removing it is tracked as open work.

## Known limits

- The Copilot usage endpoint is private and may change.
- A second Codex home and additional Claude config directories need real-machine verification when configured.
- Releases are unsigned and ship through GitHub Releases only; automatic update works for installed copies from v0.2.7 on (0.2.5 and 0.2.6 need one manual installer run).

The Settings UI follows `docs/ui/multi-account-mock.html`. Existing single-account settings are represented as primary rows and remain editable in their existing service cards. To show another account in the native strip, enable its "In strip" preference; extras default off.
