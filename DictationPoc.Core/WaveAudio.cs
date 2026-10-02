namespace DictationPoc.Core;

public sealed record WaveAudio(float[] Samples, int SampleRate, int Channels);
