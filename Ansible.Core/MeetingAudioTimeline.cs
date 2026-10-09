namespace Ansible.Core;

public sealed class MeetingAudioTimeline
{
    private const int Capacity = 16000 * 30;
    private readonly object _gate = new();
    private readonly float[] _samples = new float[Capacity];
    private readonly Queue<MeetingAudioActivity> _activity = new();
    private long _end;

    public void Append(byte[] pcm)
    {
        if (pcm.Length % 2 != 0) throw new ArgumentException("Complete PCM16 samples are required.");
        lock (_gate)
        {
            for (var index = 0; index < pcm.Length; index += 2)
                _samples[(_end++) % Capacity] = (short)(pcm[index] | pcm[index + 1] << 8) / 32768f;
            while (_activity.TryPeek(out var oldest) && oldest.StartSample + oldest.Samples < _end - Capacity)
                _activity.Dequeue();
        }
    }

    public void Observe(MeetingAudioActivity activity)
    {
        lock (_gate)
        {
            // Source evidence also covers capture queued ahead of inference.
            if (_activity.Count >= 2250) _activity.Dequeue();
            _activity.Enqueue(activity);
        }
    }

    public (WaveAudio Audio, long StartSample)? Clip(long end)
    {
        lock (_gate)
        {
            if (end <= 0 || end > _end || end <= _end - Capacity) return null;
            var start = Math.Max(0, _end - Capacity);
            var samples = new float[checked((int)(end - start))];
            for (var index = 0; index < samples.Length; index++) samples[index] = _samples[(start + index) % Capacity];
            return (new(samples, 16000, 1), start);
        }
    }

    public MeetingAudioSource Source(double startSeconds, double endSeconds)
    {
        var start = (long)Math.Floor(startSeconds * 16000);
        var end = (long)Math.Ceiling(endSeconds * 16000);
        if (end <= start) return MeetingAudioSource.Mixed;
        lock (_gate)
        {
            var covered = start;
            bool microphone = false, playback = false;
            foreach (var activity in _activity)
            {
                var activityEnd = activity.StartSample + activity.Samples;
                if (activityEnd <= start) continue;
                if (activity.StartSample >= end) break;
                if (activity.StartSample > covered) return MeetingAudioSource.Mixed;
                covered = Math.Max(covered, activityEnd);
                microphone |= activity.MicrophoneRms >= 0.003;
                playback |= activity.PlaybackRms >= 0.003;
            }
            // Levels identify an exclusive channel; echo or overlap cannot prove speaker identity.
            if (covered < end || microphone == playback) return MeetingAudioSource.Mixed;
            return microphone ? MeetingAudioSource.Microphone : MeetingAudioSource.Playback;
        }
    }
}
