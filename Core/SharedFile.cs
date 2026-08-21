namespace TheoTransfer.Core;

public sealed class SharedFile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime AddedAt { get; } = DateTime.Now;
    public string SizeText => TransferRecord.FormatSize(Size);
}
