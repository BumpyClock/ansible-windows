using System.Runtime.InteropServices;
using System.Text.Json;
using Ansible;
using Ansible.Core;

namespace Ansible.Tests;

public sealed class RecordCaptureDiagnosticsTests
{
    [Fact]
    public void NativeCaptureLayoutsMatchTheCAbi()
    {
        Assert.Equal(112, Marshal.SizeOf<RecordCaptureApi.Status>());
        Assert.Equal(24, Marshal.SizeOf<RecordCaptureApi.Burst>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureCountersPersistOnlyWithDiagnosticConsent(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ansible-record-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new LocalDiagnosticLog(directory);
            log.SetEnabled(enabled);
            var factory = new WindowsRecordTakeFactory(new CaptureStore(Path.Combine(directory, "captures")), log);
            factory.ReportDiagnostics(new RecordCaptureApi.Status
            {
                MouseEvents = 120, MouseMoves = 100, MaxHookDelayMs = 125, HookDelaysOver100Ms = 2,
                MaxPresentUs = 400, MaxLoopUs = 600, MaxFenceWaitMs = 20, HooksReleasedBeforeJoin = 1
            }, factory.DiagnosticGeneration);
            await log.DisposeAsync();
            Assert.Equal(120u, factory.LastDiagnostics!.MouseEvents);
            if (!enabled) { Assert.False(Directory.Exists(directory)); return; }
            using var json = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(Path.Combine(directory, "local-diagnostics.jsonl"))));
            Assert.Equal("RecordCapture", json.RootElement.GetProperty("Stage").GetString());
            var counters = json.RootElement.GetProperty("RecordCapture");
            Assert.Equal(125u, counters.GetProperty("MaxHookDelayMs").GetUInt32());
            Assert.True(counters.GetProperty("HooksReleasedBeforeJoin").GetBoolean());
            Assert.Equal(new[] { "MouseEvents", "MouseMoves", "MaxHookDelayMs", "HookDelaysOver100Ms",
                "MaxPresentUs", "MaxLoopUs", "MaxFenceWaitMs", "HooksReleasedBeforeJoin" },
                counters.EnumerateObject().Select(property => property.Name));
            Assert.DoesNotContain(directory, json.RootElement.GetRawText());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AConsentChangeRejectsCountersFromThePreviousTakeGeneration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ansible-record-consent-" + Guid.NewGuid().ToString("N"));
        await using var log = new LocalDiagnosticLog(directory);
        log.SetEnabled(true);
        var generation = log.CurrentGeneration;
        log.SetEnabled(false);
        log.SetEnabled(true);
        var factory = new WindowsRecordTakeFactory(new CaptureStore(Path.Combine(directory, "captures")), log);
        factory.ReportDiagnostics(new(), generation);
        await log.DisposeAsync();
        Assert.False(Directory.Exists(directory));
    }
}
