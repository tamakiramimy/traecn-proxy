namespace TrancnProxy;

public sealed class ProxyInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private ProxyInstanceLock(FileStream stream) => _stream = stream;

    public static ProxyInstanceLock Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var stream = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new ProxyInstanceLock(stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"数据目录已有 trancn-proxy 实例运行: {dataDirectory}", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}