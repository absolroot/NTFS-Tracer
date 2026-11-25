#if MAIL_VIEWER
namespace MailViewer.App;
#else
namespace JournalDesk;
#endif

internal static class SortCycle
{
    internal static bool? Next(bool sameColumn, bool descending) =>
        sameColumn ? descending ? null : true : false;
}
