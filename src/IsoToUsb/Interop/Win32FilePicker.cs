using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Controls.Dialogs;

namespace IsoToUsb.Interop;

/// <summary>
/// Win32 file-open dialog wrapper.
/// </summary>
/// <remarks>
/// We deliberately avoid <see cref="Windows.Storage.Pickers.FileOpenPicker"/>
/// because in <em>elevated, unpackaged</em> WinUI 3 apps it silently returns
/// <c>null</c> without ever showing a dialog — the WinRT picker only works
/// from MSIX identity or non-elevated processes. <c>GetOpenFileNameW</c> is
/// the supported Win32 alternative; it produces the modern Vista-style
/// explorer dialog (via <c>OFN_EXPLORER</c>) and works in any process.
/// </remarks>
internal static class Win32FilePicker
{
    /// <summary>
    /// Shows a single-file open dialog. Filter is "label1\0pattern1\0label2\0pattern2\0".
    /// Returns the selected path or <c>null</c> if the user cancelled.
    /// </summary>
    public static unsafe string? PickFile(IntPtr ownerHwnd, string title, string filter)
    {
        const int BufferLength = 1024;
        Span<char> buffer = stackalloc char[BufferLength];

        ReadOnlySpan<char> filterSpan = (filter + "\0").AsSpan();
        ReadOnlySpan<char> titleSpan = (title + "\0").AsSpan();

        fixed (char* pBuffer = buffer)
        fixed (char* pFilter = filterSpan)
        fixed (char* pTitle = titleSpan)
        {
            var ofn = new OPENFILENAMEW
            {
                // OPENFILENAMEW contains delegate / PWSTR-wrapped fields, so the
                // C# `sizeof()` operator reports the managed CLR layout (which
                // doesn't match what GetOpenFileNameW expects and fails with
                // CDERR_STRUCTSIZE = 0x0001). Marshal.SizeOf returns the
                // unmanaged size (152 bytes on x64), which is what the OS wants.
                lStructSize = (uint)Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = new HWND(ownerHwnd),
                lpstrFilter = pFilter,
                lpstrFile = pBuffer,
                nMaxFile = BufferLength,
                lpstrTitle = pTitle,
                Flags = OPEN_FILENAME_FLAGS.OFN_FILEMUSTEXIST
                      | OPEN_FILENAME_FLAGS.OFN_PATHMUSTEXIST
                      | OPEN_FILENAME_FLAGS.OFN_EXPLORER
                      | OPEN_FILENAME_FLAGS.OFN_HIDEREADONLY
                      | OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR,
            };

            if (PInvoke.GetOpenFileName(ref ofn))
            {
                return new string(pBuffer);
            }
            // Distinguish a clean cancel (CommDlgExtendedError returns 0)
            // from a real dialog failure such as a too-short buffer. The
            // previous behaviour silently returned null on every false
            // return, hiding broken-filter / out-of-memory / stale-HWND
            // bugs as "user cancelled".
            var extError = (uint)PInvoke.CommDlgExtendedError();
            if (extError == 0)
            {
                return null;
            }
            throw new InvalidOperationException(
                $"GetOpenFileName failed (CommDlgExtendedError=0x{extError:X4}).");
        }
    }
}
