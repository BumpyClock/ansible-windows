using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;

namespace Ansible;

// Explicit engineering entry point. It never starts normal dictation or microphone capture.
internal static partial class RecordEncoderSpike
{
    internal static async void Run(Application app, string localFolder, bool borderless)
    {
        var directory = Path.Combine(localFolder, "record-spikes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        try
        {
            var package = Windows.ApplicationModel.Package.Current;
            report.AppendLine($"package={package.Id.FullName}");
            report.AppendLine($"executable={Environment.ProcessPath}");
            report.AppendLine($"framework={RuntimeInformation.FrameworkDescription}");
            report.AppendLine($"dynamic-code={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
            report.AppendLine($"os={Environment.OSVersion}");
            await Task.Run(() =>
            {
                var result = new Result { Size = (uint)Marshal.SizeOf<Result>() };
                string output = Path.Combine(directory, "capture.mp4");
                var started = Stopwatch.GetTimestamp();
                int hr = RunNative(output, 5, borderless ? 1 : 0, 0, ref result);
                report.AppendLine($"capture-hresult=0x{hr:X8}");
                report.AppendLine($"wall-ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
                report.AppendLine($"frames={result.Frames}");
                report.AppendLine($"size={result.Width}x{result.Height}");
                report.AppendLine($"border-required={result.BorderRequired}");
                report.AppendLine($"borderless-setter-hresult=0x{result.BorderlessHResult:X8}");
                report.AppendLine($"start-qpc={result.StartQpc}");
                report.AppendLine($"qpc-frequency={result.QpcFrequency}");
                report.AppendLine($"first-frame-100ns={result.FirstFrame100ns}");
                report.AppendLine($"last-frame-100ns={result.LastFrame100ns}");
                Marshal.ThrowExceptionForHR(hr);
                hr = InspectNative(output, out uint frames, out uint audio, out long duration);
                report.AppendLine($"inspect-hresult=0x{hr:X8}");
                report.AppendLine($"h264-samples={frames}");
                report.AppendLine($"audio-streams={audio}");
                report.AppendLine($"duration-100ns={duration}");
                report.AppendLine($"bytes={new FileInfo(output).Length}");
                Marshal.ThrowExceptionForHR(hr);
                if (frames == 0 || audio != 0) throw new IOException("Silent H.264 validation failed.");
            });
            report.AppendLine("outcome=passed");
        }
        catch (Exception error)
        {
            report.AppendLine($"outcome=failed hresult=0x{error.HResult:X8}");
            report.AppendLine(error.GetType().Name);
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), report.ToString());
            app.Exit();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public uint Size, Frames, Width, Height;
        public int BorderRequired, BorderlessHResult;
        public long StartQpc, QpcFrequency, FirstFrame100ns, LastFrame100ns;
    }

    [LibraryImport("record_encoder_spike.dll", EntryPoint = "record_encoder_run", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RunNative(string output, uint seconds, int borderless, nint cancellation, ref Result result);

    [LibraryImport("record_encoder_spike.dll", EntryPoint = "record_encoder_inspect", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int InspectNative(string input, out uint frames, out uint audioStreams, out long duration100ns);
}
