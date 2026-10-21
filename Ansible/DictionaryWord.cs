namespace Ansible;

public sealed record DictionaryWord(string Word)
{
    public string EditName => $"Edit {Word}";
    public string RemoveName => $"Remove {Word}";
    public override string ToString() => Word;
}
