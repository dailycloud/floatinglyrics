using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FloatingLyrics.Services;

/// <summary>
/// Lets the user add a font from a .ttf/.otf/.ttc file: picks the file, reads its family name
/// from the OpenType 'name' table and installs it for the current user only (no admin rights),
/// exactly like Windows' "Install for me".
/// </summary>
public static class FontFileInstaller
{
    public sealed record Result(string FamilyName, string InstalledPath);

    /// <summary>Shows the Win32 open-file dialog on its own STA thread; null when cancelled.</summary>
    public static Task<string?> PickFontFileAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(ShowOpenDialog());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Installs the font for the current user and returns its family name.</summary>
    public static Result Install(string sourcePath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        var family = ReadFamilyName(bytes)
            ?? throw new InvalidDataException("В файле не найдено имя шрифта (это точно .ttf/.otf?)");

        var fontsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");
        Directory.CreateDirectory(fontsDir);

        var target = Path.Combine(fontsDir, Path.GetFileName(sourcePath));
        if (File.Exists(target) && new FileInfo(target).Length != bytes.Length)
        {
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(sourcePath);
            for (var i = 1; File.Exists(target); i++)
            {
                target = Path.Combine(fontsDir, $"{stem}_{i}{ext}");
            }
        }

        if (!File.Exists(target))
        {
            File.WriteAllBytes(target, bytes);
        }

        var kind = Path.GetExtension(target).Equals(".otf", StringComparison.OrdinalIgnoreCase) ? "OpenType" : "TrueType";
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts"))
        {
            key.SetValue($"{ReadFullName(bytes) ?? family} ({kind})", target);
        }

        // Make it usable right away in this session and tell other apps about it.
        _ = AddFontResourceW(target);
        _ = SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001D /* WM_FONTCHANGE */, IntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out _);
        return new Result(family, target);
    }

    // ---------------------------------------------------------------- OpenType 'name' table

    private static string? ReadFamilyName(byte[] data) => ReadName(data, 16) ?? ReadName(data, 1);

    private static string? ReadFullName(byte[] data) => ReadName(data, 4);

    private static string? ReadName(byte[] data, int nameId)
    {
        try
        {
            var offset = 0;
            if (ReadTag(data, 0) == "ttcf")
            {
                offset = (int)ReadUInt32(data, 12); // first font of the collection
            }

            int numTables = ReadUInt16(data, offset + 4);
            for (var i = 0; i < numTables; i++)
            {
                var record = offset + 12 + (i * 16);
                if (ReadTag(data, record) != "name")
                {
                    continue;
                }

                var table = (int)ReadUInt32(data, record + 8);
                int count = ReadUInt16(data, table + 2);
                var strings = table + ReadUInt16(data, table + 4);
                string? fallback = null;
                for (var n = 0; n < count; n++)
                {
                    var r = table + 6 + (n * 12);
                    int platform = ReadUInt16(data, r);
                    int language = ReadUInt16(data, r + 4);
                    if (ReadUInt16(data, r + 6) != nameId)
                    {
                        continue;
                    }

                    int length = ReadUInt16(data, r + 8);
                    var start = strings + ReadUInt16(data, r + 10);
                    if (start + length > data.Length)
                    {
                        continue;
                    }

                    var value = platform is 0 or 3
                        ? Encoding.BigEndianUnicode.GetString(data, start, length)
                        : Encoding.Latin1.GetString(data, start, length);
                    value = value.Trim('\0', ' ');
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    if (platform == 3 && language == 0x0409)
                    {
                        return value;
                    }

                    fallback ??= value;
                }

                return fallback;
            }
        }
        catch (IndexOutOfRangeException)
        {
        }
        catch (ArgumentException)
        {
        }

        return null;
    }

    private static string ReadTag(byte[] d, int o) => Encoding.ASCII.GetString(d, o, 4);

    private static int ReadUInt16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

    private static uint ReadUInt32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

    // ---------------------------------------------------------------- Win32

    private static string? ShowOpenDialog()
    {
        var buffer = Marshal.AllocHGlobal(2 * 1024 * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = "Шрифты (*.ttf;*.otf;*.ttc)\0*.ttf;*.otf;*.ttc\0Все файлы\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = 2048,
                lpstrTitle = "Выберите файл шрифта",
                Flags = 0x00001000 | 0x00000800 | 0x00080000 | 0x00000008 // FILEMUSTEXIST | PATHMUSTEXIST | EXPLORER | NOCHANGEDIR
            };
            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceW(string lpFileName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, int flags, int timeout, out IntPtr result);
}
