using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Ansible.Core;
using Microsoft.UI.Xaml;

namespace Ansible;

// Explicit bounded engineering entry point. No microphone or destination input.
internal static partial class RecordTakeValidation
{
    internal static async void Run(Application application, string localFolder)
    {
        var directory = Path.Combine(localFolder, "record-spikes", "take-" + Guid.NewGuid().ToString("N"));
        var report = new StringBuilder();
        try
        {
            await Task.Run(() => Directory.CreateDirectory(directory));
            report.AppendLine($"package={Windows.ApplicationModel.Package.Current.Id.FullName}");
            report.AppendLine($"executable={Environment.ProcessPath}");
            report.AppendLine($"dynamic-code={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
            var store = new CaptureStore(Path.Combine(directory, "captures"));
            var factory = new WindowsRecordTakeFactory(store, null);
            var options = factory.Snapshot(true);
            var burstCount = 0;
            var discarded = false;
            var capture = await Task.Run(() => factory.StartAsync(options, _ => Interlocked.Increment(ref burstCount),
                () => discarded = true, CancellationToken.None));
            RecordMediaResult media;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                media = await capture.StopAsync(false);
            }
            finally { await capture.DisposeAsync(); }
            report.AppendLine($"ink-diagnostics={factory.LastDiagnostics}");
            report.AppendLine($"released={capture.IsReleased}");
            report.AppendLine($"discarded={discarded || media.Discarded}");
            report.AppendLine($"bursts={burstCount}");
            report.AppendLine($"pictures={media.Frames.Count}");
            report.AppendLine($"video-start-offset-ms={media.VideoStartOffset?.TotalMilliseconds:F1}");
            foreach (var warning in media.Warnings) report.AppendLine($"warning={warning}");
            if (!capture.IsReleased || discarded || media.Discarded || media.Warnings.Count > 0 ||
                media.Frames.Count == 0 || media.VideoPath is null)
                throw new IOException("The production take did not produce and release a complete capture.");
            foreach (var frame in media.Frames)
            {
                var png = await File.ReadAllBytesAsync(frame.Path);
                var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
                var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
                report.AppendLine($"picture={frame.Path} size={width}x{height} bytes={png.Length}");
                if (Math.Max(width, height) > 1920) throw new IOException("Picture exceeds the long-edge cap.");
            }
            await Task.Run(() =>
            {
                Marshal.ThrowExceptionForHR(Inspect(media.VideoPath, out var frames, out var audio, out var duration));
                report.AppendLine($"h264-samples={frames}");
                report.AppendLine($"audio-streams={audio}");
                report.AppendLine($"duration-100ns={duration}");
                if (frames == 0 || audio != 0 || duration <= 0) throw new IOException("Silent MP4 inspection failed.");
            });
            report.AppendLine("outcome=passed");
        }
        catch (Exception error)
        {
            report.AppendLine($"outcome=failed hresult=0x{error.HResult:X8} {error.GetType().Name}");
            report.AppendLine(error.Message);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), report.ToString());
            application.Exit();
        }
    }

    [LibraryImport("record_encoder_spike.dll", EntryPoint = "record_encoder_inspect", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Inspect(string path, out uint frames, out uint audioStreams, out long duration100ns);
}
