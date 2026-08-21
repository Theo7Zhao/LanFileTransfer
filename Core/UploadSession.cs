using System.IO;

namespace TheoTransfer.Core;

public sealed class UploadSession : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string FileName { get; init; }
    public required string TempPath { get; init; }
    public required long TotalSize { get; init; }
    public FileStream? Stream { get; set; }
    public long Received { get; set; }
    public bool Completed { get; set; }
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    public TransferRecord? Record { get; set; }
    public SemaphoreSlim Sem { get; } = new(1, 1);

    public void Dispose()
    {
        Stream?.Dispose();
        Stream = null;
        Sem.Dispose();
    }
}
