using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OneArr.Infrastructure.Files;

/// <summary>
/// Win32 calls used for real filesystem identity: the final (resolved) path of an entry, the volume it lives on, and
/// NTFS hardlinks. Only called when OperatingSystem.IsWindows() is true.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsNative
{
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // needed to open directories
    private const uint VolumeNameDos = 0;
    private const int ErrorNotSameDevice = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    /// <summary>Number of directory entries (hardlinks) pointing at the file, or null if it cannot be read.</summary>
    public static uint? LinkCount(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid) return null;
        return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

    /// <summary>
    /// The path the OS actually resolves to, with junctions, symlinks, mount points and 8.3 names expanded and the
    /// canonical casing applied. Returns null if the entry cannot be opened.
    /// </summary>
    public static string? FinalPath(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid) return null;
        var sb = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, VolumeNameDos);
        if (length == 0 || length >= sb.Capacity) return null;
        var text = sb.ToString(0, (int)length);
        if (text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + text[8..];
        return text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }

    /// <summary>Serial number of the volume holding the entry, or null if it cannot be read.</summary>
    public static uint? VolumeSerial(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid) return null;
        return GetFileInformationByHandle(handle, out var info) ? info.VolumeSerialNumber : null;
    }

    /// <summary>
    /// Creates an NTFS hardlink. Refuses when the two paths are on different volumes, so the result is never a cross-volume
    /// copy in disguise. Returns false (with the reason) when the filesystem or volume does not allow it.
    /// </summary>
    public static bool TryCreateHardLink(string existingFile, string newFile, out string reason)
    {
        var sourceVolume = VolumeSerial(existingFile);
        var destinationVolume = VolumeSerial(Path.GetDirectoryName(newFile)!);
        if (sourceVolume is null || destinationVolume is null)
        {
            reason = "could not read volume identity";
            return false;
        }
        if (sourceVolume != destinationVolume)
        {
            reason = "source and destination are on different volumes";
            return false;
        }
        if (CreateHardLinkW(newFile, existingFile, IntPtr.Zero))
        {
            reason = "";
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        reason = error == ErrorNotSameDevice ? "not the same device" : $"CreateHardLink failed (Win32 error {error})";
        return false;
    }

    private static SafeFileHandle Open(string path) =>
        CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
}
