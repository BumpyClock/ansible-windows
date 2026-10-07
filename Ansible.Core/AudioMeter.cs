namespace Ansible.Core;

public static class AudioMeter
{
    public static double Pcm16Level(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
        {
            throw new ArgumentException("Audio level requires complete PCM16 samples.");
        }
        double sum = 0;
        for (var index = 0; index < bytes.Length; index += 2)
        {
            var sample = (short)(bytes[index] | bytes[index + 1] << 8) / 32768.0;
            sum += sample * sample;
        }
        return Math.Sqrt(sum / (bytes.Length / 2));
    }

    public static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                count++;
                inWord = true;
            }
        }
        return count;
    }
}
