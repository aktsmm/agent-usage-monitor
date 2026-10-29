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
| Codex | separate `CODEX_HOME` folders per account | the configured default home returned one usage snapshot on the real machine; a second home has not been configured or verified |
| Claude Code | separate `CLAUDE_CONFIG_DIR` / statusline snapshot per account, with a manual label | the configured snapshot returned two usage snapshots on the real machine; additional config directories remain unverified, and snapshots cannot be bound to accounts automatically (see README) |
| Copilot | one profile per GitHub CLI login or VS Code GitHub session (`Login`, source-specific identity) | `gh auth status` logins and fresh VS Code bridge snapshots can be discovered from the Add Account dialog |

## Automatic discovery

For Copilot, the Add Account dialog reads `gh auth status --hostname github.com --json hosts` only when the user asks to find CLI accounts. It groups duplicate logins and offers only successful GitHub CLI logins; it does not switch the active login. VS Code accounts use the companion extension in `integrations/vscode`: the user selects accounts and approves VS Code's authentication prompt, then the extension calls GitHub and writes quota-only snapshots locally. The app reads those snapshots and verifies the pinned GitHub account ID. Profiles with the same login, or the same verified GitHub account ID across CLI and VS Code, cannot be added twice; already-configured Copilot accounts with the same verified ID render only once in the compact strip. The profile is saved only when the user saves Settings.

The Add Account dialog states these detection conditions on screen: only GitHub CLI logins with a successful status are listed, and Codex and Claude are never detected automatically (paths are entered by hand). When a search returns nothing, the dialog says why instead of staying empty. A settings file that lists the same account twice (for example after hand editing) is rejected on load and save with the same duplicate error, instead of polling the account twice.

The GitHub CLI account list is separate from VS Code's sign-in list. The VS Code bridge uses the public authentication API rather than scanning VS Code's credential store; it refreshes every five minutes only while the extension host is running. It never gives the token to the desktop app. The usage endpoint is private and may reject VS Code-issued sessions. The standalone GitHub Copilot desktop app is not integrated. Codex does not expose a list of arbitrary `CODEX_HOME` directories, and Claude snapshots cannot be reliably associated with multiple `CLAUDE_CONFIG_DIR` accounts, so those additional profiles still require explicit paths and identity labels.

## Before releasing

Multi-account is not part of a published release yet (latest is v0.2.5; automatic update would deliver it to every installed copy, so it must not ship on unverified paths). Gate before any release of this work:

- Real-machine checks recorded as done: a non-active `gh` login returns Usage, a second `CODEX_HOME` returns Usage, Claude snapshots stay bound to the labelled account, and the Add Account / Settings screens were checked visually and with the keyboard.
- Decide whether the VS Code bridge ships at all. It is unverified against a real VS Code session, depends on a private endpoint, and is not installed by the app.
- Rebase on the current `main` (the branch was behind by several commits), bump the version, update release notes, and run tests plus the Release build.
- Pushing, tagging and publishing need the repository owner's explicit approval (the release workflow is gated by the `release-approval` environment).

## Not done yet

- The VS Code bridge's GitHub `copilot_internal/user` call is not verified with a real VS Code-issued session; the endpoint is private and may reject it.
- A second Codex home and additional Claude config directories need real-machine verification when configured.
- The new Settings UI has not been visually checked in a running Windows session.

The Settings UI follows `docs/ui/multi-account-mock.html`. Existing single-account settings are represented as primary rows and remain editable in their existing service cards. To show another account in the native strip, enable its "In strip" preference; extras default off.
