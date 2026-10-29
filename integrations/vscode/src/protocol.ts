export interface GitHubIdentity {
    id: string;
    login: string;
}

export interface CopilotUsage {
    copilot_plan?: string;
    quota_reset_date?: string;
    quota_reset_date_utc?: string;
    quota_snapshots: Record<string, Record<string, boolean | number>>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function parseGitHubIdentity(value: unknown): GitHubIdentity {
    if (!isRecord(value) || typeof value.id !== "number" || !Number.isSafeInteger(value.id) || value.id <= 0 ||
        typeof value.login !== "string" || !/^[A-Za-z0-9-]{1,39}$/.test(value.login)) {
        throw new Error("github_identity_invalid");
    }
    return { id: String(value.id), login: value.login };
}

export function sanitizeCopilotUsage(value: unknown): CopilotUsage {
    if (!isRecord(value) || !isRecord(value.quota_snapshots)) {
        throw new Error("copilot_usage_invalid");
    }
    const usage: CopilotUsage = { quota_snapshots: {} };
    for (const key of ["copilot_plan", "quota_reset_date", "quota_reset_date_utc"] as const) {
        const field = value[key];
        if (field !== undefined) {
            if (typeof field !== "string" || field.length > 128) {
                throw new Error("copilot_usage_invalid");
            }
            usage[key] = field;
        }
    }

    const entries = Object.entries(value.quota_snapshots);
    if (entries.length === 0 || entries.length > 32) {
        throw new Error("copilot_usage_invalid");
    }
    const allowed = new Set(["unlimited", "percent_remaining", "entitlement", "remaining", "credits_used"]);
    for (const [bucket, raw] of entries) {
        if (!/^[A-Za-z0-9._-]{1,80}$/.test(bucket) || !isRecord(raw)) {
            throw new Error("copilot_usage_invalid");
        }
        const quota: Record<string, boolean | number> = {};
        for (const [name, field] of Object.entries(raw)) {
            if (!allowed.has(name)) {
                continue;
            }
            if (name === "unlimited") {
                if (typeof field !== "boolean") {
                    throw new Error("copilot_usage_invalid");
                }
            } else if (typeof field !== "number" || !Number.isFinite(field) || field < 0) {
                throw new Error("copilot_usage_invalid");
            }
            quota[name] = field as boolean | number;
        }
        usage.quota_snapshots[bucket] = quota;
    }
    return usage;
}
