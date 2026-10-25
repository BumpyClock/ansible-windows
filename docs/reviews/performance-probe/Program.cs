using System.Diagnostics;
using System.Text.Json;
using Ansible.Core;

var root = Path.GetFullPath(args[0]);
Directory.CreateDirectory(root);
var measurements = new List<Measurement>();
var cases = new List<(string Name, int Size, Func<Task> Work)>();
var now = DateTimeOffset.UtcNow;
var process = Process.GetCurrentProcess();

foreach (var minutes in new[] { 5, 30, 120 })
{
    var frames = (long)minutes * 60 * 16000;
    var tokens = Enumerable.Range(0, minutes * 150).Select(i =>
        new MeetingTimedToken(i == 0 ? "word" : " word", i * 6400L, i * 6400L + 3200)).ToArray();
    var activities = Enumerable.Range(0, minutes * 15).Select(i =>
        new SpeakerActivity("speaker_" + (i % 2), i * 64000L, (i + 1) * 64000L)).ToArray();
    TranscriptUpdate Create() => NemotronSpeakerAttribution.Create(tokens, activities, frames, false);
    var expected = string.Join(' ', Enumerable.Repeat("word", tokens.Length));
    var update = Create();
    if (update.SpeechText != expected || update.SpeakerTurns?.Count != activities.Length)
        throw new Exception("Synthetic attribution produced incorrect speech or turns.");
    var document = new MeetingDocument { CreatedAt = now, UpdatedAt = now,
        Turns = update.SpeakerTurns!, Title = "Synthetic performance review" };
    var matcher = new MeetingSpeakerMatcher();
    var store = new MeetingStore(Path.Combine(root, "save-" + minutes));
    cases.Add(("attribution", minutes, () =>
    {
        var value = Create();
        if (value.SpeechText != expected) throw new Exception("Attribution changed speech.");
        return Task.CompletedTask;
    }));
    cases.Add(("matcher-no-observations", minutes, () =>
    {
        var value = matcher.Refine(document);
        if (value.AutoSpeakerNames.Count != 0 || value.Turns.Count != document.Turns.Count)
            throw new Exception("Matcher changed synthetic document.");
        return Task.CompletedTask;
    }));
    cases.Add(("save-fsync", minutes, () => store.SaveAsync(document)));
    await store.SaveAsync(document);
    var loaded = await store.LoadAsync(document.Id);
    if (!loaded.Turns.SequenceEqual(document.Turns)) throw new Exception("Save round trip failed.");
    Console.WriteLine($"fixture save minutes={minutes} turns={document.Turns.Count} bytes={new FileInfo(Path.Combine(store.DirectoryPath, document.Id.ToString("N") + ".json")).Length}");
}

foreach (var count in new[] { 10, 100, 300 })
{
    var store = new MeetingStore(Path.Combine(root, "history-" + count));
    Directory.CreateDirectory(store.DirectoryPath);
    var turns = Enumerable.Range(0, 180).Select(i => new MeetingTurn("Speaker " + i % 2,
        i * 10, (i + 1) * 10, string.Join(' ', Enumerable.Repeat("word", 25)))) .ToArray();
    for (var i = 0; i < count; i++)
    {
        var guidBytes = new byte[16];
        BitConverter.GetBytes(i + 1).CopyTo(guidBytes, 0);
        var document = new MeetingDocument { Id = new Guid(guidBytes), CreatedAt = now,
            UpdatedAt = now, Status = MeetingStatus.Completed, Turns = turns };
        // Fixture setup outside measured region. Measured listing calls production LoadAsync/Validate.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, MeetingJsonContext.Default.MeetingDocument);
        await File.WriteAllBytesAsync(Path.Combine(store.DirectoryPath, document.Id.ToString("N") + ".json"), bytes);
    }
    cases.Add(("list-history", count, async () =>
    {
        var listed = await store.ListAsync();
        if (listed.Documents.Count != count || listed.Failures.Count != 0 || listed.Documents.Sum(d => d.Turns.Count) != count * 180)
            throw new Exception("History list lost documents or turns.");
    }));
    Console.WriteLine($"fixture history documents={count} bytes={Directory.GetFiles(store.DirectoryPath).Sum(p => new FileInfo(p).Length)}");
}

foreach (var mainMiB in new[] { 4, 32 })
{
    var directory = Path.Combine(root, "identity-" + mainMiB);
    Directory.CreateDirectory(directory);
    void Fixture(string name, int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
    }
    Fixture("audiocpp.dll", mainMiB * 1024 * 1024);
    Fixture("dependency.dll", mainMiB / 2 * 1024 * 1024);
    Fixture("sample.wav", 450000);
    var request = new DeviceBenchmarkRequest(Path.Combine(directory, "audiocpp.dll"), "unused", "unused",
        Path.Combine(directory, "sample.wav"), "synthetic", NativeBackend.Cpu, 0, 4, 512, "streaming", 0);
    var expected = DeviceBenchmark.Key(request, "synthetic-model-hash", CancellationToken.None);
    cases.Add(("auto-identity-synthetic", mainMiB, () =>
    {
        if (DeviceBenchmark.Key(request, "synthetic-model-hash", CancellationToken.None) != expected)
            throw new Exception("Identity changed for unchanged files.");
        return Task.CompletedTask;
    }));
}

foreach (var item in cases) await item.Work();
for (var run = 0; run < 5; run++)
{
    // Rotate ordering to reduce systematic warmup and background-load bias.
    foreach (var item in cases.Skip(run).Concat(cases.Take(run)))
    {
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        await item.Work();
        watch.Stop();
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        measurements.Add(new(item.Name, item.Size, run + 1, watch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocated, cpuMs));
    }
}
await File.WriteAllTextAsync(Path.Combine(root, "measurements.json"),
    JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
foreach (var group in measurements.GroupBy(m => (m.Name, m.Size)))
{
    var times = group.Select(m => m.Milliseconds).Order().ToArray();
    var allocations = group.Select(m => m.AllocatedBytes).Order().ToArray();
    Console.WriteLine($"{group.Key.Name} size={group.Key.Size} n={times.Length} median_ms={times[2]:F3} min_ms={times[0]:F3} max_ms={times[^1]:F3} median_alloc_bytes={allocations[2]}");
}
Console.WriteLine("All synthetic output checks passed. No native recognition or UI measured.");
record Measurement(string Name, int Size, int Run, double Milliseconds, long AllocatedBytes, double CpuMilliseconds);
