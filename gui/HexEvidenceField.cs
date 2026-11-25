namespace JournalDesk;

public sealed record HexField(int Start, int Length, string Label, string Value)
{
    public bool IsWarning
    {
        get; init;
    }
}
