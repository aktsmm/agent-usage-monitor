using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiUsageTray.Core;

public enum Provider { Copilot, Codex, Claude }
public enum UsageUnit { PercentagePoints, Credits, Requests, Unknown }
public enum WindowKind { Monthly, Weekly, Short, Unknown }
public enum UsageSource { CopilotHttp, CodexHttp, CodexCli, ClaudeStatusline }
public enum FailureKind
{
    Network, Timeout, Unauthorized, Forbidden, RateLimited, Server, Schema,
    MissingCredential, AccountMismatch, MissingSnapshot, Cli, Storage
}
public enum DeltaKind { Baseline, Consumption, Reset, Adjustment, DefinitionChanged }
public enum Coverage { Unmeasured, Partial, CompleteToObservation }
public enum NotificationReason { PercentageStep, Threshold, DailyGuidance, WeeklyGuidance, Reset }

public sealed record AccountKey(Provider Provider, string Id)
{
    public static AccountKey FromIdentity(Provider provider, string identity) =>
        new(provider, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
}

public sealed record QuotaKey(AccountKey Account, string Bucket);
public sealed record QuotaWindow(WindowKind Kind, DateTimeOffset? StartsAt, DateTimeOffset? ResetsAt);

public sealed record QuotaSnapshot(
    QuotaKey Key, string? Plan, UsageSource Source, DateTimeOffset ObservedAt,
    UsageUnit Unit, decimal? Used, decimal? Total, decimal? UsedPercent,
    bool Unlimited, QuotaWindow Window, decimal? CreditsUsed = null)
{
    public bool IsFresh(DateTimeOffset now, TimeSpan maximumAge) =>
        ObservedAt <= now && now - ObservedAt <= maximumAge &&
        (Window.StartsAt is null || Window.StartsAt <= ObservedAt) &&
        (Window.ResetsAt is null || now < Window.ResetsAt);

    public bool HasMeasurement => Used is >= 0 && Unit != UsageUnit.Unknown;

    public string PeriodId => string.Join("|",
        Window.StartsAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "?",
        Window.ResetsAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "?",
        Window.Kind, Unit, Total?.ToString(CultureInfo.InvariantCulture) ?? "?",
        Plan ?? "?");

    public void Validate()
    {
        if (Key is null || Key.Account is null || Window is null ||
            string.IsNullOrWhiteSpace(Key.Account.Id) || string.IsNullOrWhiteSpace(Key.Bucket) ||
            Used < 0 || Total <= 0 || UsedPercent < 0 || UsedPercent > 100 ||
            CreditsUsed < 0 || (Window.StartsAt is { } start && Window.ResetsAt is { } end && start >= end))
            throw new UsageException(FailureKind.Schema, "Invalid quota fields.");
    }
}

public sealed record UsageFailure(FailureKind Kind, string Code, DateTimeOffset AttemptedAt,
    TimeSpan? RetryAfter = null);

public sealed record FetchResult(IReadOnlyList<QuotaSnapshot> Snapshots, IReadOnlyList<UsageFailure> Failures)
{
    public bool Succeeded => Snapshots.Count > 0;
    public static FetchResult Failed(UsageFailure failure) => new([], [failure]);
}

public sealed class UsageException(FailureKind kind, string safeCode, TimeSpan? retryAfter = null)
    : Exception(safeCode)
{
    public FailureKind Kind { get; } = kind;
    public string SafeCode { get; } = safeCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IUsageProvider
{
    Task<FetchResult> FetchAsync(CancellationToken cancellationToken);
}

public sealed record UsageDelta(QuotaKey Key, string PeriodId, DateTimeOffset From,
    DateTimeOffset To, UsageUnit Unit, decimal? Amount, DeltaKind Kind);

public sealed record Guidance(decimal Daily, decimal Weekly, UsageUnit Unit, string PeriodId);
public sealed record ConsumptionSummary(decimal? Measured, decimal? Guidance,
    Coverage Coverage, UsageUnit Unit, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateTimeOffset? ObservedThrough);

public sealed record NotificationCandidate(string Id, QuotaKey Key, string PeriodId,
    string RuleId, NotificationReason Reason, DateTimeOffset CreatedAt,
    decimal[] CrossedLevels, string[] DeduplicationKeys, Coverage? Coverage = null);

public sealed record NotificationRules
{
    public decimal? PercentageStep { get; init; } = 10;
    public decimal[] Thresholds { get; init; } = [80, 90, 95];
    public bool DailyGuidance { get; init; } = true;
    public bool WeeklyGuidance { get; init; } = true;
    public bool Reset { get; init; } = true;

    public void Validate()
    {
        if (PercentageStep is < 1 or > 100 ||
            Thresholds is null || Thresholds.Any(x => x <= 0 || x > 100) || Thresholds.Distinct().Count() != Thresholds.Length)
            throw new UsageException(FailureKind.Schema, "Invalid notification rules.");
    }

}

public sealed record QuotaRule(QuotaKey Key, NotificationRules Rules, bool Enabled = true);
public sealed record ProviderBinding(Provider Provider, string SelectionId);

public sealed record PollingOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromHours(1);

    public void Validate()
    {
        if (Interval <= TimeSpan.Zero || Timeout <= TimeSpan.Zero ||
            MaximumAge < Interval || MaximumBackoff < Interval)
            throw new UsageException(FailureKind.Schema, "Invalid polling options.");
    }
}

public sealed class UsageState
{
    public int Version { get; set; } = 1;
    public NotificationRules Rules { get; set; } = new();
    public List<QuotaRule> QuotaRules { get; set; } = [];
    public List<ProviderBinding> ProviderBindings { get; set; } = [];
    public PollingOptions Polling { get; set; } = new();
    public List<QuotaSnapshot> Latest { get; set; } = [];
    public List<QuotaSnapshot> Baselines { get; set; } = [];
    public List<UsageDelta> Deltas { get; set; } = [];
    public List<NotificationCandidate> Notifications { get; set; } = [];
    public List<UsageFailure> Failures { get; set; } = [];

    public void Validate()
    {
        if (Version != 1 || Rules is null || Polling is null || QuotaRules is null || ProviderBindings is null ||
            Latest is null || Baselines is null || Deltas is null || Notifications is null || Failures is null)
            throw new UsageException(FailureKind.Storage, "Invalid state shape or version.");
        Rules.Validate();
        Polling.Validate();
        if (ProviderBindings.Select(b => b.Provider).Distinct().Count() != ProviderBindings.Count)
            throw new UsageException(FailureKind.Storage, "Duplicate provider bindings.");
        foreach (var rule in QuotaRules) rule.Rules.Validate();
        foreach (var snapshot in Latest.Concat(Baselines)) snapshot.Validate();
    }

    public void Bind(Provider provider, string selectionId)
    {
        if (ProviderBindings.SingleOrDefault(b => b.Provider == provider)?.SelectionId != selectionId)
            Latest.RemoveAll(s => s.Key.Account.Provider == provider);
        ProviderBindings.RemoveAll(b => b.Provider == provider);
        ProviderBindings.Add(new(provider, selectionId));
    }
}
