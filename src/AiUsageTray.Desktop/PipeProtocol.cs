using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiUsageTray.Core;

namespace AiUsageTray.Desktop;

public sealed record QuotaView(QuotaKey Key, ConsumptionSummary Daily, ConsumptionSummary Weekly);
public sealed record DesktopView(UsageState State, DesktopSettings Settings,
    Dictionary<Provider, UsageFailure?> Failures, string? Error, List<QuotaView> Quotas,
    bool OpenSettings);
public sealed record DesktopRequest(string Method, DesktopSettings? Settings = null,
    NotificationRules? Defaults = null, List<QuotaRule>? QuotaRules = null);
public sealed record DesktopResponse(bool Success, string? Error = null, DesktopView? View = null);

public static class PipeProtocol
{
    public const int MaximumMessageBytes = 1_048_576;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, StateStore.JsonOptions);
        if (payload.Length > MaximumMessageBytes)
            throw new UsageException(FailureKind.Schema, "ipc_message_too_large");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > MaximumMessageBytes)
            throw new UsageException(FailureKind.Schema, "ipc_invalid_message_size");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, StateStore.JsonOptions)
            ?? throw new UsageException(FailureKind.Schema, "ipc_empty_message");
    }

    public static async Task<DesktopView> SendAsync(string name, DesktopRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(3000, deadline.Token);
        await WriteAsync(client, request, deadline.Token);
        var response = await ReadAsync<DesktopResponse>(client, deadline.Token);
        if (!response.Success)
            throw new UsageException(FailureKind.Storage, response.Error ?? "ipc_request_failed");
        return response.View ?? throw new UsageException(FailureKind.Schema, "ipc_view_missing");
    }
}
