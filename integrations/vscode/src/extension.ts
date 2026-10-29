import * as vscode from "vscode";
import * as path from "node:path";
import { randomUUID } from "node:crypto";
import { CopilotUsage, GitHubIdentity, parseGitHubIdentity, sanitizeCopilotUsage } from "./protocol";

interface SelectedAccount {
    id: string;
    label: string;
    userId?: string;
}

interface ManifestAccount extends GitHubIdentity {
    label: string;
    observedAt: string;
}

interface Manifest {
    version: 1;
    accounts: ManifestAccount[];
}

class BridgeError extends Error {
    constructor(readonly code: string) {
        super(code);
    }
}

class CopilotBridge implements vscode.Disposable {
    private readonly output = vscode.window.createOutputChannel("Agent Usage Monitor");
    private refreshTask: Promise<number> | undefined;
    private readonly subscriptions: vscode.Disposable[] = [];
    private readonly directory: string;

    constructor(private readonly context: vscode.ExtensionContext) {
        const localAppData = process.env.LOCALAPPDATA;
        if (process.platform !== "win32" || !localAppData) {
            throw new BridgeError("windows_local_app_data_unavailable");
        }
        this.directory = path.join(localAppData, "AiUsageTray", "vscode-bridge");
        this.subscriptions.push(
            vscode.commands.registerCommand("agentUsageMonitor.connectGithubAccounts", () => this.connect()),
            vscode.commands.registerCommand("agentUsageMonitor.clearLocalSnapshots", () => this.clearLocalSnapshots()),
            vscode.authentication.onDidChangeSessions(event => {
                if (event.provider.id === "github") {
                    void this.refreshSelected();
                }
            })
        );
        const timer = setInterval(() => void this.refreshSelected(), 5 * 60 * 1000);
        this.subscriptions.push({ dispose: () => clearInterval(timer) });
    }

    async start(): Promise<void> {
        await this.refreshSelected();
    }

    private async connect(): Promise<void> {
        try {
            const accounts = await vscode.authentication.getAccounts("github");
            if (accounts.length === 0) {
                await vscode.window.showInformationMessage("No GitHub account is signed in to VS Code.");
                return;
            }
            const previous = this.context.globalState.get<SelectedAccount[]>("selectedAccounts", []);
            const selectedIds = new Set(previous.map(account => account.id));
            const selected = await vscode.window.showQuickPick(
                accounts.map(account => ({ label: safeLabel(account.label), account, picked: selectedIds.has(account.id) })),
                { canPickMany: true, placeHolder: "Choose GitHub accounts to share Copilot quota with Agent Usage Monitor" }
            );
            if (!selected?.length) {
                return;
            }

            const connected: SelectedAccount[] = [];
            const successful = new Map((await this.readManifest()).map(account => [account.id, account]));
            const selectedAccountIds = new Set(selected.map(item => item.account.id));
            for (const old of previous) {
                if (!selectedAccountIds.has(old.id) || !old.userId) continue;
                connected.push(old);
            }
            for (const item of selected) {
                try {
                    const session = await vscode.authentication.getSession("github", [], {
                        account: item.account,
                        createIfNone: { detail: "Read GitHub Copilot quota for Agent Usage Monitor. The token stays in VS Code." }
                    });
                    if (session.account.id !== item.account.id) {
                        throw new BridgeError("github_session_account_mismatch");
                    }
                    const snapshot = await this.fetchSnapshot(session.accessToken);
                    await this.writeSnapshot(snapshot.identity, snapshot.usage, snapshot.observedAt);
                    const selectedAccount = { id: item.account.id, label: safeLabel(item.account.label), userId: snapshot.identity.id };
                    const index = connected.findIndex(account => account.id === item.account.id);
                    if (index >= 0) connected[index] = selectedAccount;
                    else connected.push(selectedAccount);
                    successful.set(snapshot.identity.id, {
                        ...snapshot.identity,
                        label: selectedAccount.label,
                        observedAt: snapshot.observedAt.toISOString()
                    });
                } catch (error) {
                    this.logError(error);
                }
            }
            await this.context.globalState.update("selectedAccounts", connected);
            await this.writeManifest([...successful.values()].filter(account =>
                connected.some(selectedAccount => selectedAccount.userId === account.id)));
            await vscode.window.showInformationMessage(
                `Agent Usage Monitor connected ${connected.length} of ${selected.length} selected account(s).`
            );
        } catch (error) {
            this.logError(error);
            await vscode.window.showErrorMessage("Could not connect VS Code GitHub accounts. Open the Agent Usage Monitor output for a safe error code.");
        }
    }

    private async refreshSelected(): Promise<number> {
        if (this.refreshTask) {
            return this.refreshTask;
        }
        this.refreshTask = this.refreshCore().finally(() => {
            this.refreshTask = undefined;
        });
        return this.refreshTask;
    }

    private async refreshCore(): Promise<number> {
        const selected = this.context.globalState.get<SelectedAccount[]>("selectedAccounts", []);
        if (!selected.length) {
            return 0;
        }
        try {
            const available = await vscode.authentication.getAccounts("github");
            const byId = new Map(available.map(account => [account.id, account]));
            const successful = new Map((await this.readManifest()).map(account => [account.id, account]));
            for (const choice of selected) {
                const account = byId.get(choice.id);
                if (!account) {
                    continue;
                }
                try {
                    const session = await vscode.authentication.getSession("github", [], {
                        account,
                        silent: true
                    });
                    if (!session || session.account.id !== choice.id) {
                        continue;
                    }
                    const snapshot = await this.fetchSnapshot(session.accessToken);
                    const observedAt = snapshot.observedAt.toISOString();
                    await this.writeSnapshot(snapshot.identity, snapshot.usage, snapshot.observedAt);
                    choice.userId = snapshot.identity.id;
                    choice.label = safeLabel(account.label);
                    successful.set(snapshot.identity.id, {
                        ...snapshot.identity,
                        label: choice.label,
                        observedAt
                    });
                } catch (error) {
                    this.logError(error);
                }
            }
            const accounts = [...successful.values()].filter(item =>
                selected.some(choice => choice.userId === item.id));
            await this.writeManifest(accounts);
            await this.context.globalState.update("selectedAccounts", selected);
            return accounts.length;
        } catch (error) {
            this.logError(error);
            return 0;
        }
    }

    private async fetchSnapshot(accessToken: string): Promise<{
        identity: GitHubIdentity;
        usage: CopilotUsage;
        observedAt: Date;
    }> {
        const identity = parseGitHubIdentity(await this.getJson("/user", accessToken));
        const usage = sanitizeCopilotUsage(await this.getJson("/copilot_internal/user", accessToken));
        return { identity, usage, observedAt: new Date() };
    }

    private async getJson(route: string, accessToken: string): Promise<unknown> {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 15000);
        try {
            const response = await fetch(`https://api.github.com${route}`, {
                method: "GET",
                redirect: "error",
                signal: controller.signal,
                headers: {
                    Authorization: `token ${accessToken}`,
                    Accept: "application/json",
                    "User-Agent": "AgentUsageMonitor-VSCode/0.1"
                }
            });
            if (!response.ok) {
                throw new BridgeError(`github_http_${response.status}`);
            }
            const declaredSize = Number(response.headers.get("content-length") ?? 0);
            if (declaredSize > 1_048_576 || !response.body) {
                throw new BridgeError("github_response_too_large");
            }
            const reader = response.body.getReader();
            const chunks: Uint8Array[] = [];
            let size = 0;
            while (true) {
                const result = await reader.read();
                if (result.done) {
                    break;
                }
                size += result.value.byteLength;
                if (size > 1_048_576) {
                    await reader.cancel();
                    throw new BridgeError("github_response_too_large");
                }
                chunks.push(result.value);
            }
            const bytes = new Uint8Array(size);
            let offset = 0;
            for (const chunk of chunks) {
                bytes.set(chunk, offset);
                offset += chunk.byteLength;
            }
            try {
                return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) as unknown;
            } catch {
                throw new BridgeError("github_response_invalid_json");
            }
        } catch (error) {
            if (error instanceof BridgeError) {
                throw error;
            }
            throw new BridgeError(error instanceof DOMException && error.name === "AbortError"
                ? "github_request_timeout" : "github_request_failed");
        } finally {
            clearTimeout(timer);
        }
    }

    private async writeSnapshot(identity: GitHubIdentity, usage: CopilotUsage, observedAt: Date): Promise<void> {
        await vscode.workspace.fs.createDirectory(vscode.Uri.file(this.directory));
        const content = {
            version: 1,
            login: identity.login,
            userId: identity.id,
            observedAt: observedAt.toISOString(),
            usage
        };
        await this.writeAtomic(`${identity.id}.json`, content);
    }

    private async writeManifest(accounts: ManifestAccount[]): Promise<void> {
        const manifest: Manifest = {
            version: 1,
            accounts: accounts.sort((a, b) => a.login.localeCompare(b.login))
        };
        await vscode.workspace.fs.createDirectory(vscode.Uri.file(this.directory));
        await this.writeAtomic("accounts.json", manifest);
    }

    private async readManifest(): Promise<ManifestAccount[]> {
        try {
            const file = vscode.Uri.file(path.join(this.directory, "accounts.json"));
            const bytes = await vscode.workspace.fs.readFile(file);
            if (bytes.byteLength > 65_536) {
                throw new BridgeError("manifest_too_large");
            }
            const manifest = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) as Manifest;
            if (manifest.version !== 1 || !Array.isArray(manifest.accounts)) {
                throw new BridgeError("manifest_invalid");
            }
            return manifest.accounts.filter(account =>
                typeof account?.id === "string" && /^\d{1,20}$/.test(account.id));
        } catch (error) {
            if (error instanceof vscode.FileSystemError && error.code === "FileNotFound") {
                return [];
            }
            if (error instanceof BridgeError) throw error;
            throw new BridgeError("manifest_read_failed");
        }
    }

    private async writeAtomic(name: string, value: unknown): Promise<void> {
        const target = vscode.Uri.file(path.join(this.directory, name));
        const temporary = vscode.Uri.file(path.join(this.directory, `${name}.${randomUUID()}.tmp`));
        try {
            await vscode.workspace.fs.writeFile(temporary, new TextEncoder().encode(JSON.stringify(value)));
            await vscode.workspace.fs.rename(temporary, target, { overwrite: true });
        } catch (error) {
            try {
                await vscode.workspace.fs.delete(temporary);
            } catch {
                this.output.appendLine("snapshot_temp_cleanup_failed");
            }
            throw error;
        }
    }

    private async clearLocalSnapshots(): Promise<void> {
        const answer = await vscode.window.showWarningMessage(
            "Delete Agent Usage Monitor's local VS Code quota snapshots and forget selected accounts?",
            { modal: true },
            "Delete local snapshots"
        );
        if (answer !== "Delete local snapshots") {
            return;
        }
        try {
            const entries = await vscode.workspace.fs.readDirectory(vscode.Uri.file(this.directory));
            for (const [name, type] of entries) {
                if ((name === "accounts.json" || /^\d+\.json$/.test(name)) &&
                    (type & vscode.FileType.File) !== 0) {
                    await vscode.workspace.fs.delete(vscode.Uri.file(path.join(this.directory, name)));
                }
            }
            await this.context.globalState.update("selectedAccounts", []);
            await vscode.window.showInformationMessage("Local VS Code quota snapshots were cleared.");
        } catch (error) {
            this.logError(error);
            await vscode.window.showErrorMessage("Could not clear local snapshots. Open the Agent Usage Monitor output for a safe error code.");
        }
    }

    private logError(error: unknown): void {
        this.output.appendLine(error instanceof BridgeError ? error.code : "operation_failed");
        this.output.show(true);
    }

    dispose(): void {
        for (const subscription of this.subscriptions) {
            subscription.dispose();
        }
        this.output.dispose();
    }
}

function safeLabel(label: string): string {
    const normalized = label.replace(/[\u0000-\u001f\u007f]/g, " ").trim().slice(0, 128);
    return normalized.length > 0 ? normalized : "GitHub account";
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const bridge = new CopilotBridge(context);
    context.subscriptions.push(bridge);
    await bridge.start();
}

export function deactivate(): void {}
