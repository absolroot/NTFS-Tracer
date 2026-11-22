using System.Runtime.InteropServices;
using System.Diagnostics;

namespace EvidenceImage;

// Windows x64 ABI from libewf 20260924. Load only from the application directory.
internal static class EwfNative
{
    private const string Dll = "JournalDesk.libewf.dll";
    [DllImport(Dll)] internal static extern IntPtr libewf_get_version();
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int libewf_glob_wide(string path, nuint length, byte format, out IntPtr names, out int count, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_glob_wide_free(IntPtr names, int count, IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_initialize(out IntPtr handle, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_open_wide(IntPtr handle, IntPtr names, int count, int access, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_free(ref IntPtr handle, IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_get_media_size(IntPtr handle, out ulong length, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_segment_files_corrupted(IntPtr handle, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_segment_files_encrypted(IntPtr handle, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_get_number_of_checksum_errors(IntPtr handle, out int count, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_get_number_of_acquiry_errors(IntPtr handle, out int count, out IntPtr error);
    [DllImport(Dll)] internal static extern nint libewf_handle_read_buffer_at_offset(IntPtr handle, IntPtr buffer, nuint count, long offset, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_handle_get_root_file_entry(IntPtr handle, out IntPtr entry, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_free(ref IntPtr entry, IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_number_of_sub_file_entries(IntPtr entry, out int count, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_sub_file_entry(IntPtr entry, int index, out IntPtr child, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_utf16_name_size(IntPtr entry, out nuint size, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_utf16_name(IntPtr entry, [Out] ushort[] name, nuint size, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_type(IntPtr entry, out byte type, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_size(IntPtr entry, out ulong size, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_utf8_hash_value_sha1(IntPtr entry, [Out] byte[] text, nuint size, out IntPtr error);
    [DllImport(Dll)] internal static extern int libewf_file_entry_get_utf8_hash_value_md5(IntPtr entry, [Out] byte[] text, nuint size, out IntPtr error);
    [DllImport(Dll)] internal static extern nint libewf_file_entry_read_buffer_at_offset(IntPtr entry, IntPtr buffer, nuint count, long offset, out IntPtr error);
    [DllImport(Dll)] private static extern int libewf_error_sprint(IntPtr error, byte[] text, nuint size);
    [DllImport(Dll)] private static extern void libewf_error_free(ref IntPtr error);

    static EwfNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(EwfNative).Assembly, (name, _, _) =>
            name == Dll ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, Dll)) : IntPtr.Zero);
    }
    internal static void ReleaseEntry(ref IntPtr entry)
    {
        ReportReleaseFailure(libewf_file_entry_free(ref entry, IntPtr.Zero), "file entry");
    }

    internal static void ReportReleaseFailure(int result, string resource)
    {
        if (result != 1)
            Trace.TraceWarning($"libewf could not release the {resource}.");
    }

    internal static long Check(long result, IntPtr error)
    {
        try
        {
            if (result >= 0)
                return result;
            var text = new byte[2048];
            int length = error == IntPtr.Zero ? 0 : libewf_error_sprint(error, text, (nuint)text.Length);
            string reason = length <= 0 ? "Invalid or unsupported EWF data." : System.Text.Encoding.UTF8.GetString(text).TrimEnd('\0', '\r', '\n');
            throw new InvalidDataException(reason);
        }
        finally { if (error != IntPtr.Zero) libewf_error_free(ref error); }
    }
}
