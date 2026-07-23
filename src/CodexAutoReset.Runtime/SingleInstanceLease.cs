namespace CodexAutoReset.Runtime;

public sealed class SingleInstanceLease : IDisposable
{
    private FileStream? stream;

    private SingleInstanceLease(FileStream stream)
    {
        this.stream = stream;
    }

    public static SingleInstanceLease? TryAcquire(RuntimePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.RootDirectory);

        try
        {
            var stream = new FileStream(
                paths.InstanceLockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return new SingleInstanceLease(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
}
