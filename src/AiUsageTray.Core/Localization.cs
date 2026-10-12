using System.Globalization;
using System.Resources;

namespace AiUsageTray.Core;

public enum DisplayLanguage { System, Japanese, English }

public enum UiText
{
    AgentUsage, Brand, HistoryTitle, History, HistoryStorage, AppearanceLanguage, DisplayLanguage,
    FollowSystem, Japanese, English, LanguageExplanation, LanguageInvariantNote,
    Refresh, Reload, Settings, Save, Cancel, Confirm, Close, Service, AccountHash, Quota,
    Range, RangeDays, MonthInput, ShowMonth, PeriodDefinition, PlanUnknown, UnitUnknown,
    PercentagePoints, Credits, Requests, PercentagePointsExplanation, CalendarExplanation,
    HistorySubtitle, HistoryLegend, HistoryNotes, Unmeasured, Partial, CompleteToObservation,
    ObservedConsumption, DailyGuidance, GuidanceUsed, LastObservation, NoRecord, Changes,
    DayBoundaryUnallocated, ObservationGap, DayDetail, HistoryLoading, NoRecordedQuota,
    HistoryLocation, Saved, PendingRecords, ChooseFolder, FolderPickerTitle, OpenHistoryFolder,
    ShowHistoryFile, OpenArchiveFolder, ShowArchiveFile, StorageNotes, ArchiveHeading,
    ArchiveNotes, CreateVerifyArchive, VerifyArchive, ArchiveConfirmationTitle, ArchiveConfirmation,
    ArchiveCreatedVerified, ArchiveVerified, OpenDoesNotVerify, MigrationTitle, MigrationPreview,
    FreeSpaceUnknown, MigrationCompleted, OperationFailed, UnknownError, HistoryUnavailable,
    MonthInvalid, RangeMissing, CatalogMissing, HistoryResponseMissing, FolderRequired,
    PreviewRequired, PreviewMissing, FolderNotWritable, MonthRequired, OpenLocationRequired,
    OperationNotAllowed, FileMissing, FolderMissing, ExplorerFailed,
    InvalidHistoryData, HistoryTooLarge, WriterMismatch, ExistingHistoryUnavailable,
    MigrationOverlap, MigrationConflict, MigrationStale, MigrationExtraFiles, MigrationSpace,
    MigrationNotReady, MigrationVerifyFailed, CurrentMonthArchive, PendingArchive,
    ArchivedMonthSealed, NoMonthRecorded, NoRawRecorded, ArchiveInvalid,
    StorageUnavailable, LocalJournalUnavailable, UnsupportedLinkedFolder,
    Notifications, Connections, AppearanceStartup, UsedRemaining, Stale, Fetched, LastConfirmed,
    UnknownUsage, Unlimited, ResetAt, ResetUnknown, Daily, Weekly, GuidanceExceedance,
    GuidanceUsage, ConsumptionOfGuidance, FixedAllocation, GuidanceUnknown,
    ProviderMissing, ClaudeMissing, RetrievalStatus, PollingFooter, DemoFooter,
    MainTitle, Exit, UpdateCheck, StartAtLogon, StartupEnableConfirm, StartupDisableConfirm,
    InitializationFailed, PanelStartFailed, CommunicationFailed, CommunicationStopped,
    CommunicationTimeout, RefreshFailed, SaveFailed, AuthenticationRequired, AccountMismatch,
    MissingCredential, ProviderError, NotificationUsage, NotificationGuidance, NotificationReset,
    UpdateTitle, UpdateChecking, UpdateAvailable, UpdateCurrent, UpdateUnavailable,
    UpdateDownloadConfirm, UpdateDownloading, UpdateDeferred, UpdateApplyConfirm,
    UpdateRestart, UpdateFailed, PortableUpdate, UpdateProfileBlocked,
    ShowUsageStrip, UsageStripNotes, UsageStripAmbiguous, MonthlyQuota, WeeklyQuota, ShortQuota,
    UnknownQuotaWindow, QuotaDetails, ScalarWithoutDenominator, CreditsApiValue, MainSubtitle,
    UpdateIdle, UpdateDisabled, FailureUnauthorized, FailureForbidden, FailureNetwork, FailureTimeout,
    FailureSchema, FailureRateLimited, FailureServer, FailureSnapshot, FailureCli, FailureStorage,
    ClaudeCliMissing, ClaudeLoginRequired, ClaudeScalarRequired, GithubCliMissing, CodexFileMissing,
    CodexLoginRequired, CodexNotSubscription, CodexStoreUnsupported, DiscoveryTimeout, DiscoveryCancelled,
    DiscoveryReady, DiscoveryUnknown, ConnectionDisabled, ConnectionConfirm, ConnectionDiscovering,
    ConnectionAutomatic, ConnectionManual, ConnectionPending, OpenReleasePageConfirm,
    SettingsTitle, EnableQuotaNotifications, NotifyDaily, NotifyWeekly, NotifyReset, NotifySteps,
    FetchCopilot, FetchCodex, FetchClaude, CodexFallback, AutoUpdateOption, CancelDetection,
    UnsavedChanges, UnsavedConfirm, NotificationQuota, DefaultQuota, StepInput, ThresholdInput,
    NotificationNotes, ConnectionIntro, CandidateNote, LegacyConnectionNote, LegacyEnable, LegacyKeep,
    DetectAgain, CheckState, UseCandidate, CandidateConfirm, GithubLoginInput, GithubTargetInput,
    CodexEmailInput, CodexHomeInput, CodexExecutableInput, CodexFallbackNote, ClaudeFileInput,
    ClaudeLabelInput, ClaudeNote, ConnectionAdvanced, CredentialNotes, Appearance, Dark, Light,
    StartupNote, UpdateBehavior, UpdateVersionNote, SavedAccount, CheckedAt, DetectedCandidate,
    AutoGuidance, AutoGuidanceUnknown, NotificationNumberInvalid, SettingsInvalid, PinnedAccountInvalid,
    AutoPinRequired, CandidateMissing, ClaudeNeedsConfirmation, StartupExecutableRequired,
    PendingSettingsRequired, NotificationSettingsRequired, QuotaSettingsRequired, VerificationStartupBlocked,
    DemoReadOnly, PanelRequired, ConsumptionViewMissing, HostStopped
}

public sealed class UiLocalizer
{
    private static readonly ResourceManager Resources = new("AiUsageTray.Core.Resources.UiStrings",
        typeof(UiLocalizer).Assembly);

    public DisplayLanguage Language { get; }
    public CultureInfo Culture { get; }
    public string LanguageTag => Language == DisplayLanguage.Japanese ? "ja" : "en";

    public UiLocalizer(DisplayLanguage preference = DisplayLanguage.System, CultureInfo? systemCulture = null)
    {
        if (!Enum.IsDefined(preference))
            throw new UsageException(FailureKind.Schema, "language_preference_invalid");
        Language = preference == DisplayLanguage.System
            ? (systemCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "ja"
                ? DisplayLanguage.Japanese : DisplayLanguage.English
            : preference;
        Culture = CultureInfo.GetCultureInfo(Language == DisplayLanguage.Japanese ? "ja-JP" : "en-US");
    }

    public static ResourceSet ResourceSet(DisplayLanguage language)
    {
        if (language is not (DisplayLanguage.Japanese or DisplayLanguage.English))
            throw new UsageException(FailureKind.Schema, "language_resource_set_invalid");
        return Resources.GetResourceSet(language == DisplayLanguage.Japanese
                ? CultureInfo.GetCultureInfo("ja") : CultureInfo.InvariantCulture,
                createIfNotExists: true, tryParents: false)
            ?? throw new UsageException(FailureKind.Storage, "language_resources_missing");
    }

    public string Text(UiText key)
    {
        if (!Enum.IsDefined(key))
            throw new UsageException(FailureKind.Schema, "language_key_invalid");
        return ResourceSet(Language).GetString(key.ToString()) is { Length: > 0 } value
            ? value : throw new UsageException(FailureKind.Storage, "language_resource_missing:" + key);
    }

    public string Format(UiText key, params object?[] arguments) =>
        string.Format(Culture, Text(key), arguments);

    public string Unit(UsageUnit unit) => Text(unit switch
    {
        UsageUnit.PercentagePoints => UiText.PercentagePoints,
        UsageUnit.Credits => UiText.Credits,
        UsageUnit.Requests => UiText.Requests,
        _ => UiText.UnitUnknown
    });

    public string Coverage(Coverage coverage) => Text(coverage switch
    {
        global::AiUsageTray.Core.Coverage.Partial => UiText.Partial,
        global::AiUsageTray.Core.Coverage.CompleteToObservation => UiText.CompleteToObservation,
        _ => UiText.Unmeasured
    });

    public string Error(string code)
    {
        var normalized = StableErrorCode(code);
        var key = normalized switch
        {
            "history_file_too_large" => UiText.HistoryTooLarge,
            "history_version_or_writer_mismatch" or "history_journal_writer_mismatch" => UiText.WriterMismatch,
            "history_existing_folder_unavailable" => UiText.ExistingHistoryUnavailable,
            "history_migration_overlapping_folders" => UiText.MigrationOverlap,
            "history_migration_destination_conflict" => UiText.MigrationConflict,
            "history_migration_preview_stale" or "history_migration_destination_changed" => UiText.MigrationStale,
            "history_migration_destination_extra_files" or "history_migration_unknown_file" => UiText.MigrationExtraFiles,
            "history_migration_insufficient_space" => UiText.MigrationSpace,
            "history_migration_preview_not_ready" => UiText.MigrationNotReady,
            "history_migration_verify_failed" => UiText.MigrationVerifyFailed,
            "history_current_month_cannot_archive" => UiText.CurrentMonthArchive,
            "history_archive_pending_records" => UiText.PendingArchive,
            "history_archived_month_is_sealed" => UiText.ArchivedMonthSealed,
            "history_month_not_recorded" => UiText.NoMonthRecorded,
            "history_month_raw_missing" => UiText.NoRawRecorded,
            "history_reparse_point_not_supported" => UiText.UnsupportedLinkedFolder,
            "history_month_invalid" => UiText.MonthInvalid,
            "history_range_missing" => UiText.RangeMissing,
            "history_catalog_missing" => UiText.CatalogMissing,
            "history_response_missing" => UiText.HistoryResponseMissing,
            "history_folder_required" => UiText.FolderRequired,
            "history_preview_required" => UiText.PreviewRequired,
            "history_preview_missing" => UiText.PreviewMissing,
            "history_folder_not_writable" => UiText.FolderNotWritable,
            "history_month_required" => UiText.MonthRequired,
            "history_open_location_required" => UiText.OpenLocationRequired,
            "history_operation_not_allowed" => UiText.OperationNotAllowed,
            "history_file_missing" => UiText.FileMissing,
            "history_folder_missing" => UiText.FolderMissing,
            "history_explorer_failed" => UiText.ExplorerFailed,
            "settings_invalid" => UiText.SettingsInvalid,
            "settings_pin_invalid" => UiText.PinnedAccountInvalid,
            "settings_auto_pin_required" => UiText.AutoPinRequired,
            "settings_candidate_missing" => UiText.CandidateMissing,
            "settings_claude_confirmation_required" => UiText.ClaudeNeedsConfirmation,
            "settings_startup_executable_required" => UiText.StartupExecutableRequired,
            "settings_notification_number_invalid" => UiText.NotificationNumberInvalid,
            "settings_connection_missing" => UiText.PendingSettingsRequired,
            "settings_notification_missing" => UiText.NotificationSettingsRequired,
            "settings_quota_missing" => UiText.QuotaSettingsRequired,
            "verification_startup_blocked" => UiText.VerificationStartupBlocked,
            "demo_read_only" or "demo_history_not_saved" => UiText.DemoReadOnly,
            "panel_required" => UiText.PanelRequired,
            "consumption_view_missing" => UiText.ConsumptionViewMissing,
            _ when normalized.StartsWith("history_archive_", StringComparison.Ordinal) => UiText.ArchiveInvalid,
            _ when normalized.StartsWith("history_storage_unavailable", StringComparison.Ordinal) => UiText.StorageUnavailable,
            _ when normalized.StartsWith("history_local_journal_unavailable", StringComparison.Ordinal) => UiText.LocalJournalUnavailable,
            _ when normalized.StartsWith("history_", StringComparison.Ordinal) => UiText.InvalidHistoryData,
            _ => UiText.UnknownError
        };
        return key == UiText.UnknownError ? Format(key, code) : Text(key) + " (" + normalized + ")";
    }

    public static string StableErrorCode(string code) => code switch
    {
        "対象月をyyyy-MM形式で入力してください。" => "history_month_invalid",
        "履歴の表示範囲がありません。" => "history_range_missing",
        "履歴のカタログがありません。" => "history_catalog_missing",
        "履歴の応答がありません。" => "history_response_missing",
        "保存先のフォルダーを選択してください。" => "history_folder_required",
        "保存先のプレビューを再確認してください。" => "history_preview_required",
        "保存先のプレビューがありません。" => "history_preview_missing",
        "保存先へ書き込めません。" => "history_folder_not_writable",
        "対象月を選択してください。" => "history_month_required",
        "開く場所を選択してください。" => "history_open_location_required",
        "履歴の操作が許可されていません。" => "history_operation_not_allowed",
        "対象の履歴ファイルはまだ作成されていません。" => "history_file_missing",
        "対象の履歴フォルダーはまだ作成されていません。" => "history_folder_missing",
        "Explorerを起動できませんでした。" or "Explorerを起動できませんでした。権限・保存先を確認してください。"
            => "history_explorer_failed",
        "接続設定のアカウント・パスを確認してください。" => "settings_invalid",
        "固定アカウント情報が不正です。" => "settings_pin_invalid",
        "自動接続には確認済みの固定アカウントが必要です。" => "settings_auto_pin_required",
        "確認済みの接続候補がありません。" => "settings_candidate_missing",
        "Claude数値連携は本人の確認が必要です。" => "settings_claude_confirmation_required",
        "ログオン時起動は公開済みEXEから設定してください。" => "settings_startup_executable_required",
        "通知の数値を確認してください。" => "settings_notification_number_invalid",
        "接続設定がありません。" => "settings_connection_missing",
        "通知設定がありません。" => "settings_notification_missing",
        "枠設定がありません。" => "settings_quota_missing",
        "検証プロファイルからログオン設定は変更しません。" => "verification_startup_blocked",
        "画面はトレイから起動してください。" => "panel_required",
        "表示用の観測差分がありません。" => "consumption_view_missing",
        "デモは保存・接続しません。" or "デモは検出・更新・保存しません。" => "demo_read_only",
        _ => code
    };
}
