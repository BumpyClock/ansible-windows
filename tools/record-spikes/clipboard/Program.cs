using System.Globalization;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("""
                ClipboardSpike self-check [--output <folder>]
                ClipboardSpike publish --case text|files|files-text|bitmap|composite [options]
                ClipboardSpike paste --case text|files|files-text|bitmap|composite|sequential [options]
                Options: --count 1..32 (default 8; text/bitmap 1), --width 320..1920 (default 1280), --noise,
                         --countdown 3..30 (default 8), --pace 500..10000 (default 1500),
                         --target <target-label>, --output <folder>
                publish replaces the clipboard, leaves content available, and sends no keys.
                paste queues Ctrl+V after a countdown. Focus a disposable chat draft. Never submits.
                sequential publishes and pastes each bitmap, then text, at the requested pace.
                Timers are pacing only. They never verify attachment insertion or consumption.
                self-check replaces the clipboard with synthetic fixtures and sends no keys.
                """);
            return 0;
        }
        string command = args[0];
        if (command is not ("self-check" or "publish" or "paste")) throw new ArgumentException("Unknown command. Use help.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            if (key is not ("--case" or "--count" or "--width" or "--noise" or "--countdown" or "--pace" or "--target" or "--output"))
                throw new ArgumentException($"Unknown option {key}.");
            string value = key == "--noise" ? "true" : ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {key}.");
            if (!options.TryAdd(key, value)) throw new ArgumentException($"Duplicate option {key}.");
        }
        string Option(string key, string fallback) => options.GetValueOrDefault(key, fallback);
        int Number(string key, int fallback, int min, int max)
        {
            if (!int.TryParse(Option(key, fallback.ToString(CultureInfo.InvariantCulture)), out int number) || number < min || number > max)
                throw new ArgumentException($"{key} must be {min}..{max}.");
            return number;
        }
        string kind = Option("--case", "files-text");
        if (kind is not ("text" or "files" or "files-text" or "bitmap" or "composite" or "sequential")) throw new ArgumentException("Unknown case.");
        if (kind == "sequential" && command != "paste") throw new ArgumentException("Sequential needs explicit paste command.");
        int count = Number("--count", kind is "text" or "bitmap" ? 1 : 8, 1, 32), width = Number("--width", 1280, 320, 1920);
        if (kind == "bitmap" && count != 1) throw new ArgumentException("Bitmap publishes one image. Use sequential for many bitmap pastes.");
        int countdown = Number("--countdown", 8, 3, 30), pace = Number("--pace", 1500, 500, 10000);
        string output = Path.GetFullPath(Option("--output", ".runtime/record-spikes/clipboard"));
        string run = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid().ToString("N")[..6]}";
        string folder = Path.Combine(output, run);
        Directory.CreateDirectory(folder);
        PrepareMatrix(Path.Combine(output, "manual-results.csv"));
        var images = Enumerable.Range(1, command == "self-check" ? 3 : count).Select(i => TestImage.Frame(i, width, options.ContainsKey("--noise"))).ToArray();
        var paths = new string[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            paths[i] = Path.Combine(folder, $"synthetic-frame-{i + 1:00}.png");
            File.WriteAllBytes(paths[i], images[i].Png());
        }
        var sheet = TestImage.Composite(images);
        string sheetPath = Path.Combine(folder, "synthetic-contact-sheet.png");
        File.WriteAllBytes(sheetPath, sheet.Png());
        string text = $"SYNTHETIC CLIPBOARD TEST {run}\r\nCase {kind}. Fixture count {images.Length}. No speech or screen capture.\r\nFrame order is numbered. This is an unsent acceptance test.";
        File.WriteAllText(Path.Combine(folder, "text.txt"), text);
        File.WriteAllText(Path.Combine(folder, "files.csv"), "file,bytes,width,height\r\n" +
            string.Join("\r\n", paths.Select((p, i) => $"{Csv(p)},{new FileInfo(p).Length},{images[i].Width},{images[i].Height}")) +
            $"\r\n{Csv(sheetPath)},{new FileInfo(sheetPath).Length},{sheet.Width},{sheet.Height}\r\n");

        Dictionary<uint, byte[]> Payload(string mode, int image = 0)
        {
            var payload = new Dictionary<uint, byte[]>();
            if (mode != "files" && mode != "image-only") payload.Add(Clipboard.UnicodeText, Clipboard.Text(text));
            if (mode is "files" or "files-text") payload.Add(Clipboard.FileDrop, Clipboard.Files(paths));
            if (mode is "bitmap" or "composite" or "image-only")
            {
                var bitmap = mode == "composite" ? sheet : images[image];
                payload.Add(Clipboard.Format("PNG"), bitmap.Png());
                payload.Add(Clipboard.DibV5, bitmap.DibV5());
            }
            return payload;
        }

        if (command == "self-check")
        {
            foreach (string mode in new[] { "text", "files", "files-text", "bitmap", "composite" })
            {
                var payload = Payload(mode);
                Clipboard.Publish(payload);
                Clipboard.Verify(payload, mode is "files" or "files-text" ? paths : null);
                Console.WriteLine($"PASS {mode}. Exact bytes and three privacy formats read back" + (mode is "files" or "files-text" ? "; shell file paths read back." : "."));
            }
            Console.WriteLine($"Fixtures {folder}. No input events sent. Target acceptance remains pending.");
            return 0;
        }

        string log = Path.Combine(folder, "publication.csv");
        File.WriteAllText(log, "utc,run,target,case,frame,formats,bytes,input,attachmentVerification\r\n");
        nint target = 0;
        if (command == "paste")
        {
            Console.WriteLine($"Focus a disposable chat draft. Ctrl+V begins in {countdown} seconds. Keep this target focused. Nothing is submitted.");
            for (int left = countdown; left > 0; left--) { Console.WriteLine(left); Thread.Sleep(1000); }
            target = Clipboard.Foreground;
        }
        void Publish(string mode, int index)
        {
            var payload = Payload(mode, index);
            Clipboard.Publish(payload);
            uint sequence = Clipboard.Sequence;
            Clipboard.Verify(payload, mode is "files" or "files-text" ? paths : null);
            string sent = "none";
            if (command == "paste") { Clipboard.Paste(target, sequence); sent = "Ctrl+V queued"; }
            File.AppendAllText(log, $"{DateTime.UtcNow:O},{run},{Csv(Option("--target", "unspecified"))},{kind},{index + 1},{Csv(string.Join(';', payload.Keys))},{payload.Values.Sum(v => (long)v.Length)},{sent},manual pending\r\n");
        }
        if (kind == "sequential")
        {
            for (int i = 0; i < images.Length; i++)
            {
                Publish("image-only", i);
                Thread.Sleep(pace);
            }
            Publish("text", 0);
        }
        else Publish(kind, 0);
        Console.WriteLine($"Published {kind}. Clipboard left available. Files {folder}.");
        Console.WriteLine($"Record observed attachments and text in {Path.Combine(output, "manual-results.csv")}. Queued input is unverified.");
        return 0;
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static void PrepareMatrix(string path)
    {
        if (File.Exists(path)) return;
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true));
        writer.WriteLine("target,browserOrAppVersion,osBuild,dateUtc,case,requestedFrames,fixtureWidth,noise,paceMs,runId,pngBytesTotal,attachedCount,textPreserved,orderPreserved,result,observedCountLimit,observedPerFileByteLimit,observedTotalByteLimit,readableLabels,fallback,notes");
        foreach (string target in new[] { "claude.ai", "chatgpt.com", "GitHub Copilot app chat", "Copilot coding agent task box", "Codex cloud task box" })
        foreach (var test in new[] { ("text", 0), ("files", 1), ("files", 8), ("files-text", 1), ("files-text", 8), ("bitmap", 1), ("sequential", 8), ("composite", 8) })
            writer.WriteLine($"{Csv(target)},,,,{test.Item1},{test.Item2},,,,,,,,,pending,,,,,,");
    }
}
