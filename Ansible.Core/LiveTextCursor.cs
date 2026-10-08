namespace Ansible.Core;

public sealed class LiveTextCursor
{
    public string SentText { get; private set; } = "";

    public string Pending(string recognizedSpeech)
    {
        if (!recognizedSpeech.StartsWith(SentText, StringComparison.Ordinal))
            throw new InvalidDataException("The model revised text that was already inserted. Live insertion stopped without rewriting it.");
        return recognizedSpeech[SentText.Length..];
    }

    public void ConfirmSent(string recognizedSpeech)
    {
        _ = Pending(recognizedSpeech);
        SentText = recognizedSpeech;
    }
}
