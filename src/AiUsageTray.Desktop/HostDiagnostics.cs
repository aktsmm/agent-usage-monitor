using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageTray.Desktop;

[JsonConverter(typeof(JsonStringEnumConverter<HostEvent>))]
public enum HostEvent
{
    Started, Ready, Heartbeat, PreviousRunIncomplete, ShutdownRequested, ShutdownCancelled,
    Cancelling, Draining, Saving, Disposing, LoopReturned, CleanExit, Fatal,
    PollingStopped, IpcStopped, DiscoveryFailed, StripFailed, PanelExited, UpdateHelperExited,
    WindowClose, SessionEnding, SessionEnded, PowerSuspend, PowerResume,
    PollingFault, IpcFault, DiscoveryFault
}

[JsonConverter(typeof(JsonStringEnumConverter<HostShutdownReason>))]
public enum HostShutdownReason { None, TrayMenu, WindowClose, Update, InitializationFailed }

public sealed record HostRunRecord(DateTimeOffset At, Guid RunId, int Pid, string Version,
    HostEvent Event, HostShutdownReason Reason, int? ExitCode, string? ExceptionType, string[]? Frames);

/// <summary>Local lifecycle evidence only; never serialize exception messages or source paths.</summary>
public sealed class HostDiagnostics
{
    public const int MaximumLogBytes = 131_072;
    private readonly object gate = new();
    private readonly string logPath;
    private readonly string markerPath;
    private readonly Guid runId = Guid.NewGuid();
    private readonly string version;

    // The caller must hold the profile's StateStore lease before opening these shared files.
    public HostDiagnostics(string profile, string version)
    {
        this.version = version;
        var folder = Path.Combine(profile, "diagnostics");
        Directory.CreateDirectory(folder);
        logPath = Path.Combine(folder, "host.jsonl");
        markerPath = Path.Combine(folder, "host-run.json");
        if (File.Exists(markerPath))
        {
            if (new FileInfo(markerPath).Length > 16_384)
                throw new IOException("host_marker_too_large");
            var previous = JsonSerializer.Deserialize<HostRunRecord>(File.ReadAllBytes(markerPath))
                ?? throw new JsonException("host_marker_invalid");
            if (previous.Event != HostEvent.CleanExit || previous.ExitCode != 0)
                Write(HostEvent.PreviousRunIncomplete);
        }
        Write(HostEvent.Started);
    }

    public void Write(HostEvent kind, HostShutdownReason reason = HostShutdownReason.None,
        Exception? exception = null, int? exitCode = null)
    {
        var frames = exception is null ? null : new StackTrace(exception, false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.Assembly == typeof(Program).Assembly ||
                method?.DeclaringType?.Assembly == typeof(Core.UsageState).Assembly ||
                method?.DeclaringType?.Assembly == typeof(Diagnostics.ReadOnlyProcess).Assembly)
            .Take(16).Select(method => method!.DeclaringType!.FullName + "." + method.Name).ToArray();
        var record = new HostRunRecord(DateTimeOffset.UtcNow, runId, Environment.ProcessId, version,
            kind, reason, exitCode, exception?.GetType().FullName, frames);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > 16_384) throw new IOException("host_evidence_too_large");
        lock (gate)
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length + bytes.Length + 1 > MaximumLogBytes)
                File.Move(logPath, logPath + ".previous", overwrite: true);
            using (var log = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                log.Write(bytes);
                log.WriteByte((byte)'\n');
                log.Flush(flushToDisk: true);
            }
            // Preserve the most recent liveness/exit evidence independently of log rotation.
            var temporary = markerPath + ".tmp";
            using (var marker = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                marker.Write(bytes);
                marker.Flush(flushToDisk: true);
            }
            File.Move(temporary, markerPath, overwrite: true);
        }
    }
}
