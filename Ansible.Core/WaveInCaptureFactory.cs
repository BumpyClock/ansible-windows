using Ansible.Core.Windows;

namespace Ansible.Core;

/// <summary>Starts Windows waveIn capture off the caller's thread, at 16 kHz mono PCM16.</summary>
/// <remarks>
/// Capture and backlog are limited to five minutes (7500 packets of at most 1280 bytes).
/// A failed stop can be retried on the same capture, but recording cannot be restarted.
/// When startup cleanup leaves native ownership unresolved, CaptureOwnershipException
/// transfers the capture to the caller. Retain it until IsReleased confirms cleanup.
/// </remarks>
public sealed class WaveInCaptureFactory : IAudioCaptureFactory
{
    private readonly IWaveInApi _api;

    public WaveInCaptureFactory() : this(new WaveInApi()) { }

    internal WaveInCaptureFactory(IWaveInApi api) => _api = api;

    public async Task<IAudioCapture> StartAsync(CancellationToken cancellationToken, int microphoneBoostDecibels = 0)
    {
        var capture = new WaveInCapture(_api, new MicrophoneBoost(microphoneBoostDecibels));
        try
        {
            await Task.Run(() => capture.Initialize(cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return capture;
        }
        catch (Exception startupError)
        {
            try
            {
                await capture.StopAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                var errors = new AggregateException("Microphone startup and cleanup failed.", startupError, cleanupError);
                if (!capture.IsReleased)
                {
                    throw new CaptureOwnershipException(
                        "Microphone startup failed with unresolved native ownership. Retry cleanup on Capture.", capture, errors);
                }
                throw errors;
            }
            throw;
        }
    }
}
