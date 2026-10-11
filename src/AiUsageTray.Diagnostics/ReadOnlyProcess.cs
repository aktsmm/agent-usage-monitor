using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiUsageTray.Core;

namespace AiUsageTray.Diagnostics;

public static class ReadOnlyProcess
{
    public static Process Start(string executable, IEnumerable<string> arguments, string? codexHome = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment.Remove("TYPESAFE_API_KEY");
        if (codexHome is not null)
            info.Environment["CODEX_HOME"] = codexHome;
        return Process.Start(info) ?? throw new UsageException(FailureKind.Cli, "cli_start_failed");
    }

    public static async Task<string> CaptureAsync(string executable, string[] arguments,
        CancellationToken cancellationToken)
    {
        using var process = Start(executable, arguments);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var errors = DiscardErrorsAsync(process, deadline.Token);
        try
        {
            process.StandardInput.Close();
            var output = await ReadLimitedAsync(process.StandardOutput, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            if (process.ExitCode != 0)
                throw new UsageException(FailureKind.Cli, "cli_command_failed");
            return output;
        }
        finally
        {
            await CloseAsync(process);
            try { await errors; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    public static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            output.Append(buffer, 0, count);
            if (output.Length > 1_048_576)
                throw new UsageException(FailureKind.Schema, "cli_output_too_large");
        }
        return output.ToString();
    }

    public static async Task DiscardErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await process.StandardError.ReadAsync(buffer, cancellationToken) > 0) { }
    }

    public static async Task CloseAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync();
    }

    public static async Task<JsonElement> RpcAsync(Process process, int id, string method,
        object? parameters, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        for (int count = 0; count < 500; count++)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                throw new UsageException(FailureKind.Cli, "codex_rpc_closed");
            if (line.Length > 1_048_576)
                throw new UsageException(FailureKind.Schema, "codex_rpc_too_large");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new UsageException(FailureKind.Schema, "codex_rpc_object_expected");
            if (root.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number &&
                responseId.TryGetInt32(out var number) && number == id)
            {
                if (root.TryGetProperty("error", out _))
                    throw new UsageException(FailureKind.Cli, "codex_rpc_error");
                if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new UsageException(FailureKind.Schema, "codex_rpc_result_missing");
                return result.Clone();
            }
            if (root.TryGetProperty("id", out _) && root.TryGetProperty("method", out _))
                throw new UsageException(FailureKind.Cli, "codex_unexpected_server_request");
        }
        throw new UsageException(FailureKind.Cli, "codex_rpc_notification_limit");
    }
}
