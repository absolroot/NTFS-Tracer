namespace JournalDesk;

internal static class SortCycle
{
    internal static bool? Next(bool sameColumn, bool descending) =>
        sameColumn ? descending ? null : true : false;
}
