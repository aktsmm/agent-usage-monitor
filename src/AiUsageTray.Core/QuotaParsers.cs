using System.Globalization;
using System.Text.Json;

namespace AiUsageTray.Core;

public static class QuotaParsers
{
    public static IReadOnlyList<QuotaSnapshot> Copilot(JsonElement root, AccountKey account, DateTimeOffset observedAt)
    {
        RequireObject(root);
        var plan = Text(root, "copilot_plan");
        var reset = Timestamp(root, "quota_reset_date_utc") ?? Timestamp(root, "quota_reset_date");
        DateTimeOffset? start = reset is { Day: 1, Hour: 0, Minute: 0, Second: 0 } r &&
            r.Offset == TimeSpan.Zero ? r.AddMonths(-1) : null;
        var window = new QuotaWindow(WindowKind.Monthly, start, reset);
        if (!root.TryGetProperty("quota_snapshots", out var quotas) || quotas.ValueKind != JsonValueKind.Object)
            throw Schema("copilot_quota_snapshots_missing");
        var result = new List<QuotaSnapshot>();
        foreach (var bucket in quotas.EnumerateObject())
        {
            var q = bucket.Value;
            RequireObject(q);
            bool unlimited = Flag(q, "unlimited");
            var percentRemaining = Number(q, "percent_remaining");
            var entitlement = Number(q, "entitlement");
            var remaining = Number(q, "remaining");
            var credits = Number(q, "credits_used");
            decimal? percent = percentRemaining is >= 0 and <= 100 ? 100 - percentRemaining
                : entitlement is > 0 && remaining is { } rem
                    ? Math.Clamp((entitlement.Value - rem) / entitlement.Value * 100, 0, 100) : null;
            if (percentRemaining is < 0 or > 100 || entitlement < 0)
                throw Schema("copilot_invalid_quota");
            // Keep credits as an explicit scalar, never reinterpret a request entitlement as a credit ceiling.
            var unit = percent is not null && !unlimited ? UsageUnit.PercentagePoints
                : credits is not null ? UsageUnit.Credits : UsageUnit.Unknown;
            decimal? used = unit == UsageUnit.PercentagePoints ? percent : credits;
            decimal? total = unit == UsageUnit.PercentagePoints ? 100 : null;
            var snapshot = new QuotaSnapshot(new(account, bucket.Name), plan, UsageSource.CopilotHttp,
                observedAt, used is null ? UsageUnit.Unknown : unit, used, total,
                unlimited ? null : percent, unlimited, window, credits);
            snapshot.Validate();
            result.Add(snapshot);
        }
        if (result.Count == 0)
            throw Schema("copilot_empty_quotas");
        return result;
    }

    public static IReadOnlyList<QuotaSnapshot> CodexHttp(JsonElement root, AccountKey account, DateTimeOffset observedAt)
    {
        RequireObject(root);
        var plan = Text(root, "plan_type");
        var result = new List<QuotaSnapshot>();
        if (root.TryGetProperty("rate_limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
            AddCodexWindows(result, limit, "codex", account, plan, observedAt, false);
        if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
            foreach (var bucket in additional.EnumerateArray())
            {
                var id = Text(bucket, "limit_id") ?? Text(bucket, "metered_feature");
                if (id is null)
                    throw Schema("codex_limit_id_missing");
                if (bucket.TryGetProperty("rate_limit", out var rate))
                    AddCodexWindows(result, rate, id, account, plan, observedAt, false);
            }
        return NonEmpty(result);
    }

    public static IReadOnlyList<QuotaSnapshot> CodexCli(JsonElement root, AccountKey account,
        string? plan, DateTimeOffset observedAt)
    {
        RequireObject(root);
        var result = new List<QuotaSnapshot>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object && buckets.EnumerateObject().Any())
        {
            foreach (var bucket in buckets.EnumerateObject())
                AddCodexWindows(result, bucket.Value, bucket.Name, account,
                    Text(bucket.Value, "planType") ?? plan, observedAt, true);
        }
        else if (root.TryGetProperty("rateLimits", out var limit) && limit.ValueKind == JsonValueKind.Object)
            AddCodexWindows(result, limit, Text(limit, "limitId") ?? "codex", account, plan, observedAt, true);
        return NonEmpty(result);
    }

    public static IReadOnlyList<QuotaSnapshot> ClaudeSnapshot(JsonElement root, AccountKey expectedAccount)
    {
        RequireObject(root);
        if (Text(root, "account_id") != expectedAccount.Id)
            throw new UsageException(FailureKind.AccountMismatch, "claude_snapshot_account_mismatch");
        var observedAt = Timestamp(root, "observed_at") ?? throw Schema("claude_observed_at_missing");
        if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            throw Schema("claude_rate_limits_missing");
        var result = new List<QuotaSnapshot>();
        foreach (var (name, duration) in new[] { ("five_hour", 300), ("seven_day", 10080) })
        {
            if (!limits.TryGetProperty(name, out var q) || q.ValueKind == JsonValueKind.Null)
                continue;
            var used = Number(q, "used_percentage") ?? throw Schema("claude_percentage_missing");
            var reset = Epoch(q, "resets_at");
            var snapshot = new QuotaSnapshot(new(expectedAccount, name), Text(root, "plan"),
                UsageSource.ClaudeStatusline, observedAt, UsageUnit.PercentagePoints, used, 100,
                used, false, new(duration == 10080 ? WindowKind.Weekly : WindowKind.Short,
                    reset?.AddMinutes(-duration), reset));
            snapshot.Validate();
            result.Add(snapshot);
        }
        return NonEmpty(result);
    }

    private static void AddCodexWindows(List<QuotaSnapshot> result, JsonElement limit, string id,
        AccountKey account, string? plan, DateTimeOffset observedAt, bool cli)
    {
        RequireObject(limit);
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limit.TryGetProperty(cli ? name : name + "_window", out var q) || q.ValueKind == JsonValueKind.Null)
                continue;
            RequireObject(q);
            var used = Number(q, cli ? "usedPercent" : "used_percent") ?? throw Schema("codex_percentage_missing");
            var duration = Number(q, cli ? "windowDurationMins" : "limit_window_seconds");
            if (!cli)
                duration /= 60;
            if (duration is <= 0 or > 525600)
                throw Schema("codex_invalid_window");
            var reset = Epoch(q, cli ? "resetsAt" : "reset_at");
            var kind = duration == 10080 ? WindowKind.Weekly
                : duration is > 0 ? WindowKind.Short : WindowKind.Unknown;
            var snapshot = new QuotaSnapshot(new(account, $"{id}:{name}"), plan,
                cli ? UsageSource.CodexCli : UsageSource.CodexHttp, observedAt,
                UsageUnit.PercentagePoints, used, 100, used, false,
                new(kind, duration is { } minutes ? reset?.AddMinutes(-(double)minutes) : null, reset));
            snapshot.Validate();
            result.Add(snapshot);
        }
    }

    public static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static decimal? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            throw Schema("quota_number_type_changed");
        return number;
    }

    private static bool Flag(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Schema("quota_boolean_type_changed");
        return value.GetBoolean();
    }

    private static DateTimeOffset? Timestamp(JsonElement root, string name)
    {
        var text = Text(root, name);
        if (text is null)
            return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            throw Schema("quota_timestamp_invalid");
        return time;
    }

    private static DateTimeOffset? Epoch(JsonElement root, string name)
    {
        var epoch = Number(root, name);
        if (epoch is null)
            return null;
        if (epoch != decimal.Truncate(epoch.Value) || epoch < 0 || epoch > 253402300799)
            throw Schema("quota_epoch_invalid");
        return DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value);
    }

    private static IReadOnlyList<QuotaSnapshot> NonEmpty(List<QuotaSnapshot> result) =>
        result.Count > 0 ? result : throw Schema("no_supported_quota_windows");

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Schema("quota_object_expected");
    }

    private static UsageException Schema(string code) => new(FailureKind.Schema, code);
}
