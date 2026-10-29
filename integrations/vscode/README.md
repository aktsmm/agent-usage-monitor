# VS Code GitHub Copilot bridge

This companion extension lets Agent Usage Monitor use a GitHub account already signed in to VS Code. It uses VS Code's public `authentication` API, asks VS Code to share each selected account session, and calls GitHub from the extension host. The access token is never sent to Agent Usage Monitor, written to disk, or logged.

The extension writes a small manifest and quota-only snapshots to `%LOCALAPPDATA%\AiUsageTray\vscode-bridge`. It refreshes selected accounts every five minutes while the VS Code extension host is running. The monitor can read those local snapshots even when GitHub CLI (`gh`) is not signed in. Usage data and account identity remain local; use **Agent Usage Monitor: Clear local snapshots** to remove the cache and forget selected accounts.

The quota request uses Agent Usage Monitor's existing GitHub Copilot usage endpoint, which is not a documented public API and may change or reject VS Code-issued sessions. This integration is not verified against every VS Code authentication-provider version. A failed request is reported as a sanitized error code and does not fall back to reading VS Code's credential store.

## Build and install locally

From this directory, run `npm ci`, `npm test`, create `artifacts\vsix`, then run `npm run package`. Install the generated VSIX with VS Code's **Install from VSIX…** command. No Marketplace publication is configured. `npm audit` currently reports a high advisory in `@azure/msal-node`, pulled in only by the dev-time packager `@vscode/vsce`; the packaged VSIX has no runtime dependencies (`npm audit --omit=dev` reports 0, and a test guards the empty dependency list).

After installation, run **Agent Usage Monitor: Connect GitHub accounts**, select accounts, and approve VS Code's authentication prompt. In the monitor's Add Account dialog, choose **Find VS Code accounts**, select a detected account, and save settings. The monitor reads only the allowlisted quota fields from the extension's local snapshot.
