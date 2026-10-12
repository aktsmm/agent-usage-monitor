using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public enum HistoryAction { Catalog, Query, PreviewLocation, ChangeLocation, Archive, VerifyArchive, OpenLocation }
public sealed record HistoryRequest(HistoryAction Action, HistoryQuery? Query = null,
    string? Folder = null, string? PreviewId = null, DateOnly? Month = null, HistoryLocation? Location = null);
public sealed record HistoryResponse(bool Success, string? Error = null,
    HistoryOptions? Options = null, HistoryStorageStatus? Status = null,
    List<HistoryCatalogItem>? Catalog = null, List<HistoryDay>? Days = null,
    HistoryMigrationPreview? Migration = null, string? PreviewId = null, string? File = null);

public sealed class HistoryCommands(HistoryService service)
{
    private HistoryMigrationPreview? preview;
    private string? previewId;

    public async Task<HistoryResponse> ExecuteAsync(HistoryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Action)
            {
                case HistoryAction.Catalog:
                    return new(true, Options: service.Options, Status: service.Status,
                        Catalog: (await service.Store.CatalogAsync(cancellationToken)).Items);
                case HistoryAction.Query:
                    var query = request.Query ?? throw new UsageException(FailureKind.Schema, "history_range_missing");
                    return new(true, Status: service.Status,
                        Days: (await service.Store.QueryAsync(query, cancellationToken)).ToList());
                case HistoryAction.PreviewLocation:
                    if (string.IsNullOrWhiteSpace(request.Folder) || !Path.IsPathFullyQualified(request.Folder))
                        throw new UsageException(FailureKind.Schema, "history_folder_required");
                    preview = await service.PreviewLocationAsync(request.Folder, cancellationToken);
                    previewId = Guid.NewGuid().ToString("N");
                    return new(true, Migration: preview, PreviewId: previewId);
                case HistoryAction.ChangeLocation:
                    if (preview is null || previewId is null || request.PreviewId != previewId)
                        throw new UsageException(FailureKind.Forbidden, "history_preview_required");
                    var approved = preview;
                    preview = null; previewId = null;
                    await service.ChangeLocationAsync(approved, userApproved: true, cancellationToken);
                    return new(true, Options: service.Options, Status: service.Status);
                case HistoryAction.Archive:
                    var month = request.Month ?? throw new UsageException(FailureKind.Schema, "history_month_required");
                    return new(true, File: await service.ArchiveAsync(month, DateTimeOffset.UtcNow, cancellationToken));
                case HistoryAction.VerifyArchive:
                    var verify = request.Month ?? throw new UsageException(FailureKind.Schema, "history_month_required");
                    await service.Store.VerifyArchiveAsync(verify, cancellationToken);
                    return new(true, File: service.Store.ArchivePath(verify));
                case HistoryAction.OpenLocation:
                    await HistoryLocations.OpenAsync(service.Store,
                        request.Location ?? throw new UsageException(FailureKind.Schema, "history_open_location_required"),
                        request.Month, cancellationToken);
                    return new(true);
                default:
                    throw new UsageException(FailureKind.Forbidden, "history_operation_not_allowed");
            }
        }
        catch (UsageException ex) { return new(false, ex.SafeCode, Status: service.Status); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "history_storage_unavailable:" + ex.GetType().Name, Status: service.Status);
        }
    }
}
