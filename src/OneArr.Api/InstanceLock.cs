namespace OneArr.Api;

/// <summary>
/// One running backend per configuration folder. The lock is an exclusively opened file held for the process lifetime;
/// the operating system releases it when the process exits, even after a crash, so it never goes stale.
/// </summary>
public static class InstanceLock
{
    private static FileStream? _held;

    /// <summary>Releases the lock when the host stops, so an in-process restart (tests) can take it again.</summary>
    public static void Release()
    {
        _held?.Dispose();
        _held = null;
    }

    /// <returns>True if this process now holds the lock.</returns>
    public static bool TryAcquire(string configDir)
    {
        try
        {
            _held = new FileStream(Path.Combine(configDir, "onearr.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
