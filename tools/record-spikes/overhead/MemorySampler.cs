using System.Diagnostics;

internal sealed class MemorySampler : IDisposable
{
    internal const int IntervalMs = 100;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _worker;
    private readonly List<(double Ms, long Private, long WorkingSet)> _samples = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private Exception? _failure;

    internal MemorySampler()
    {
        _worker = new Thread(Observe) { IsBackground = true, Name = "overhead-memory-sampler" };
        _worker.Start();
    }

    private void Observe()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            do
            {
                process.Refresh();
                _samples.Add((Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                    process.PrivateMemorySize64, process.WorkingSet64));
            } while (!_stop.WaitOne(IntervalMs));
        }
        catch (Exception error) { _failure = error; }
    }

    internal IReadOnlyList<(double Ms, long Private, long WorkingSet)> Finish()
    {
        _stop.Set();
        _worker.Join();
        if (_failure is not null) throw new IOException("Memory sampling failed.", _failure);
        return _samples;
    }

    public void Dispose()
    {
        _stop.Set();
        _worker.Join();
        _stop.Dispose();
    }
}
