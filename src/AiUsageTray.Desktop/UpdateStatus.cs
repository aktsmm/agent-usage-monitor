using System;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public enum UpdateStage { Idle, Checking, NoStableRelease, Current, Downloading, Deferred, Ready, Failed, Disabled, Portable }
public sealed record UpdateStatus(UpdateStage Stage, string Message, string? TargetVersion = null,
    DateTimeOffset? CheckedAt = null, DateTimeOffset? RetryAt = null, string? ErrorCode = null)
{
    public string Display(UiLocalizer locale) => Stage switch
    {
        UpdateStage.Idle => locale.Text(UiText.UpdateIdle),
        UpdateStage.Checking => locale.Text(UiText.UpdateChecking),
        UpdateStage.NoStableRelease => locale.Text(UiText.UpdateUnavailable),
        UpdateStage.Current => locale.Format(UiText.UpdateCurrent, UpdateFlow.Version),
        UpdateStage.Downloading => locale.Text(UiText.UpdateDownloading) + " " + TargetVersion,
        UpdateStage.Deferred => locale.Text(UiText.UpdateDeferred),
        UpdateStage.Ready => locale.Text(UiText.UpdateRestart) + " " + TargetVersion,
        UpdateStage.Failed => locale.Format(UiText.UpdateFailed, ErrorCode ?? "update_failed"),
        UpdateStage.Disabled => locale.Text(UiText.UpdateDisabled),
        UpdateStage.Portable => locale.Text(UiText.PortableUpdate),
        _ => locale.Format(UiText.UnknownError, "update_stage_invalid")
    };
}

public static class UpdatePolicy
{
    public static bool CanApply(bool enabled, bool automatic, bool panelAlive, bool discovering,
        bool installed, bool portable) =>
        installed && !portable && (!automatic || enabled) && !panelAlive && !discovering;
}
