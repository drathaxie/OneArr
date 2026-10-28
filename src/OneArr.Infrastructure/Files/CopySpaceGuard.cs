namespace OneArr.Infrastructure.Files;

/// <summary>
/// Read-only disk-space preflight for a verified-copy import. Hardlinks do not
/// duplicate file data and are checked separately by the importer.
/// A network share or inaccessible volume whose capacity cannot be verified
/// is refused rather than attempting a potentially incomplete copy.
/// </summary>
public static class CopySpaceGuard
{
    private const long ReserveBytes = 16L * 1024 * 1024;
    private const long MaxReserveBytes = 256L * 1024 * 1024;

    public static long RequiredBytes(long sourceLength)
    {
        if (sourceLength < 0) throw new ArgumentOutOfRangeException(nameof(sourceLength));
        var reserve = Math.Max(ReserveBytes, Math.Min(MaxReserveBytes, sourceLength / 100));
        return sourceLength > long.MaxValue - reserve ? long.MaxValue : sourceLength + reserve;
    }

    public static bool HasEnoughSpace(long availableBytes, long sourceLength) =>
        availableBytes >= RequiredBytes(sourceLength);

    public static string? CopyProblem(string destinationPath, long sourceLength)
    {
        if (sourceLength < 0) return "Source file size is not valid";
        try
        {
            var path = Path.GetFullPath(destinationPath);
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return "Cannot determine destination volume for a verified copy";
            var volume = new DriveInfo(root);
            if (!volume.IsReady)
                return "Destination volume is not ready for a verified copy";
            if (!HasEnoughSpace(volume.AvailableFreeSpace, sourceLength))
                return $"Insufficient free space for a verified copy: needs {RequiredBytes(sourceLength):N0} bytes including reserve; only {volume.AvailableFreeSpace:N0} bytes available";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "Cannot verify destination free space; refusing copy until the volume is accessible";
        }
    }
}
