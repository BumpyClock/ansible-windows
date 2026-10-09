using System.Threading.Channels;

namespace Ansible.Core;

public interface IMeetingRecognitionEngine : IAsyncDisposable
{
    Task<IReadOnlyList<AudioModel>> ConnectAsync(CancellationToken cancellationToken);
    Task StreamMeetingAsync(AudioModel model, ChannelReader<byte[]> audio, RecognitionOptions options,
        IProgress<TranscriptUpdate> progress, CancellationToken cancellationToken, Action? onReady = null);
}
