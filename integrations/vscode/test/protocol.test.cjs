const test = require("node:test");
const assert = require("node:assert/strict");
const { parseGitHubIdentity, sanitizeCopilotUsage } = require("../out/protocol.js");

test("identity parser accepts a numeric GitHub id and login", () => {
    assert.deepEqual(parseGitHubIdentity({ id: 42, login: "sample-user", name: "not persisted" }), {
        id: "42",
        login: "sample-user"
    });
});

test("identity parser rejects malformed identities", () => {
    assert.throws(() => parseGitHubIdentity({ id: 0, login: "sample-user" }), /github_identity_invalid/);
    assert.throws(() => parseGitHubIdentity({ id: 42, login: "bad/name" }), /github_identity_invalid/);
});

test("usage sanitizer stores only quota fields", () => {
    assert.deepEqual(sanitizeCopilotUsage({
        copilot_plan: "pro",
        quota_reset_date_utc: "2026-11-01T00:00:00Z",
        private_profile_data: "must not persist",
        quota_snapshots: {
            premium_interactions: {
                percent_remaining: 62,
                credits_used: 3,
                extra_user_data: "must not persist"
            }
        }
    }), {
        copilot_plan: "pro",
        quota_reset_date_utc: "2026-11-01T00:00:00Z",
        quota_snapshots: {
            premium_interactions: {
                percent_remaining: 62,
                credits_used: 3
            }
        }
    });
});

test("usage sanitizer rejects malformed or excessive quota payloads", () => {
    assert.throws(() => sanitizeCopilotUsage({ quota_snapshots: [] }), /copilot_usage_invalid/);
    assert.throws(() => sanitizeCopilotUsage({
        quota_snapshots: { premium: { percent_remaining: Number.NaN } }
    }), /copilot_usage_invalid/);
    assert.throws(() => sanitizeCopilotUsage({
        quota_snapshots: Object.fromEntries(Array.from({ length: 33 }, (_, index) => [`quota${index}`, {}]))
    }), /copilot_usage_invalid/);
});
