using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ansible.Core;

public sealed class CopilotMeetingTextCleanup : IMeetingTextCleanup
{
    private const int MaximumOutputCharacters = MeetingCleanupText.MaximumOutputCharacters;
    private readonly string? _executablePath;
    private readonly Func<ProcessStartInfo, string, CancellationToken, Task<CleanupProcessResult>> _run;
    private readonly string _scratchRoot;
    private readonly TimeSpan _timeout;

    public CopilotMeetingTextCleanup(string? executablePath = null)
        : this(executablePath, RunProcessAsync, Path.Combine(Path.GetTempPath(), "Ansible", "copilot-cleanup"), TimeSpan.FromMinutes(5)) { }

    internal CopilotMeetingTextCleanup(string? executablePath,
        Func<ProcessStartInfo, string, CancellationToken, Task<CleanupProcessResult>> run, string scratchRoot, TimeSpan timeout)
    {
        _executablePath = executablePath;
        _run = run;
        _scratchRoot = Path.GetFullPath(scratchRoot);
        _timeout = timeout;
    }

    public async Task<IReadOnlyList<string>> CleanAsync(MeetingDocument document, MeetingCleanupOptions options,
        CancellationToken cancellationToken = default)
    {
        var batches = MeetingCleanupText.Batches(document, options);
        cancellationToken.ThrowIfCancellationRequested();
        if (batches.Count == 0) return [];
        var executable = _executablePath ?? FindExecutable()
            ?? throw new InvalidOperationException("GitHub Copilot CLI was not found. Install it with winget install --id GitHub.Copilot --exact, then run copilot login and restart Ansible.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var result = new string[document.Turns.Count];
        try
        {
            var offset = 0;
            foreach (var batch in batches)
            {
                var cleaned = await CleanBatchAsync(executable, batch, options, timeout.Token);
                for (var index = 0; index < cleaned.Count; index++) result[offset + index] = cleaned[index];
                offset += batch.Count;
            }
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Copilot transcript cleanup timed out. The original transcript is preserved.");
        }
    }

    private async Task<IReadOnlyList<string>> CleanBatchAsync(string executable, IReadOnlyList<CleanupTurn> batch,
        MeetingCleanupOptions options, CancellationToken token)
    {
        var scratch = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var home = Path.Combine(scratch, "home");
            var cache = Path.Combine(scratch, "cache");
            var logs = Path.Combine(scratch, "logs");
            var work = Path.Combine(scratch, "work");
            foreach (var directory in new[] { home, cache, logs, work }) Directory.CreateDirectory(directory);
            for (var parent = new DirectoryInfo(work); parent is not null; parent = parent.Parent)
                if (Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, ".git")))
                    throw new InvalidOperationException("Copilot cleanup requires a scratch directory outside a Git repository.");
            await File.WriteAllTextAsync(Path.Combine(home, "settings.json"),
                """{"memory":false,"disableAllHooks":true,"ide":{"autoConnect":false},"notifications":false,"customAgents":{"defaultLocalOnly":true}}""", token);
            var start = CreateStartInfo(executable, home, cache, logs, work, options);
            var prompt = MeetingCleanupText.Instructions + "\n" + MeetingCleanupText.Input(batch);
            CleanupProcessResult process;
            try { process = await _run(start, prompt, token); }
            catch (Win32Exception) { throw new InvalidOperationException("GitHub Copilot CLI could not start. Install GitHub Copilot CLI, run copilot login, and restart Ansible."); }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(FailureMessage(process.Error));
            token.ThrowIfCancellationRequested();
            return ParseOutput(process.Output, batch);
        }
        finally
        {
            var resolved = Path.GetFullPath(scratch);
            if (!resolved.StartsWith(_scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The Copilot cleanup scratch path is invalid.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string home, string cache, string logs,
        string work, MeetingCleanupOptions options)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in start.Environment.Keys.ToArray())
        {
            if ((key.StartsWith("COPILOT_", StringComparison.OrdinalIgnoreCase) &&
                 key is not "COPILOT_GITHUB_TOKEN" and not "COPILOT_GH_HOST") ||
                key.StartsWith("GITHUB_COPILOT_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(key);
        }
        start.Environment["COPILOT_HOME"] = home;
        start.Environment["COPILOT_CACHE_HOME"] = cache;
        start.Environment["COPILOT_ALLOW_ALL"] = "false";
        start.Environment["COPILOT_OTEL_ENABLED"] = "false";
        start.Environment["COPILOT_AUTO_UPDATE"] = "false";
        start.Environment["USE_TGREP"] = "false";
        start.Environment["NO_COLOR"] = "1";
        foreach (var argument in new[] { "--model", options.Model, "--reasoning-effort", options.ReasoningEffort,
            "--output-format", "json", "--stream", "off", "--log-level", "none", "--log-dir", logs,
            // A nonempty allowlist avoids CLI versions interpreting an empty list as no filter.
            // This private name is never registered; every actual tool is excluded.
            "--available-tools=__ansible_transcript_cleanup_no_tools__", "--deny-tool=shell", "--deny-tool=read", "--deny-tool=write", "--deny-tool=url", "--deny-tool=memory",
            "--no-custom-instructions", "--disable-builtin-mcps", "--no-ask-user", "--no-auto-update", "--sync-session", "off",
            "--disallow-temp-dir", "--secret-env-vars=GH_TOKEN,GITHUB_TOKEN,COPILOT_GITHUB_TOKEN" }) start.ArgumentList.Add(argument);
        return start;
    }

    private static string? FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    internal static IReadOnlyList<string> ParseOutput(string output, IReadOnlyList<CleanupTurn> expected)
    {
        if (output.Length > MaximumOutputCharacters) throw InvalidResponse();
        try
        {
            string? final = null;
            var ended = false;
            using var lines = new StringReader(output);
            while (lines.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var message = JsonDocument.Parse(line, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 32 });
                var root = message.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "session.error" || type?.StartsWith("tool.", StringComparison.Ordinal) == true ||
                    type?.StartsWith("hook.", StringComparison.Ordinal) == true) throw InvalidResponse();
                if (type == "session.mcp_servers_loaded")
                {
                    foreach (var server in root.GetProperty("data").GetProperty("servers").EnumerateArray())
                        if (server.GetProperty("status").GetString() != "disabled") throw InvalidResponse();
                }
                if (type == "session.mcp_server_status_changed" &&
                    root.GetProperty("data").GetProperty("status").GetString() != "disabled") throw InvalidResponse();
                if (type == "result")
                {
                    if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True) throw InvalidResponse();
                    ended = true;
                }
                if (type != "assistant.message") continue;
                if (ended) throw InvalidResponse();
                var data = root.GetProperty("data");
                if (data.TryGetProperty("toolRequests", out var tools) && tools.ValueKind != JsonValueKind.Null && tools.GetArrayLength() != 0)
                    throw InvalidResponse();
                final = data.GetProperty("content").GetString();
            }
            if (!ended || string.IsNullOrWhiteSpace(final)) throw InvalidResponse();
            return MeetingCleanupText.ParseResponse(final, expected);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw InvalidResponse(); }
    }

    private static InvalidDataException InvalidResponse() => new("Copilot returned an incomplete or invalid cleanup response. The original transcript is preserved.");

    internal static string FailureMessage(string error)
    {
        if (error.Contains("was removed", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("was renamed", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase))
            return "The installed Copilot CLI is incompatible with this version of Ansible. Update Ansible or Copilot CLI. The original transcript is preserved.";
        if (error.Contains("model", StringComparison.OrdinalIgnoreCase))
            return "Copilot could not use the selected model or reasoning effort. Check Transcript cleanup in Settings and your Copilot model access. The original transcript is preserved.";
        if (error.Contains("login", StringComparison.OrdinalIgnoreCase) || error.Contains("auth", StringComparison.OrdinalIgnoreCase))
            return "Copilot authentication failed. Run copilot login with the installed CLI, check your Copilot access, and restart Ansible. The original transcript is preserved.";
        return "Copilot cleanup failed. Check copilot login, network access, your Copilot access, and the selected model in Settings. No transcript changes were applied.";
    }

    internal static async Task<CleanupProcessResult> RunProcessAsync(ProcessStartInfo start, string prompt, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = ReadBoundedAsync(process.StandardOutput, retain: true, token);
        var errors = ReadBoundedAsync(process.StandardError, retain: true, token);
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), token);
            process.StandardInput.Close();
            var pending = new List<Task> { process.WaitForExitAsync(token), output, errors };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            return new(process.ExitCode, await output, await errors);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            await process.WaitForExitAsync();
            try { await Task.WhenAll(output, errors); } catch { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader stream, bool retain, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var total = 0;
        while (await stream.ReadAsync(buffer.AsMemory(), token) is var read && read > 0)
        {
            total += read;
            if (total > MaximumOutputCharacters) throw InvalidResponse();
            if (retain) text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}

internal sealed record CleanupProcessResult(int ExitCode, string Output, string Error = "");
