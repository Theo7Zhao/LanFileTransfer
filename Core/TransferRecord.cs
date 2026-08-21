using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TheoTransfer.Core;

public enum TransferDirection { PhoneToPc, PcToPhone }

public enum TransferState { Waiting, Transferring, Done, Failed, Cancelled }

public sealed class TransferRecord : INotifyPropertyChanged
{
    private long _transferred;
    private TransferState _state = TransferState.Waiting;
    private string? _message;
    private string _speedText = "";
    private long _markBytes;
    private long _markTicks;

    public required TransferDirection Direction { get; init; }
    public required string FileName { get; init; }
    public required long TotalBytes { get; init; }
    public string? FilePath { get; private set; }
    public DateTime StartTime { get; } = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Transferred => Interlocked.Read(ref _transferred);

    public double Percent => TotalBytes <= 0 ? 100 : Math.Min(100.0, Transferred * 100.0 / TotalBytes);
    public string PercentText => $"{Percent:0.#}%";
    public string DirectionText => Direction == TransferDirection.PhoneToPc ? "手机 → 电脑" : "电脑 → 手机";
    public string SizeText => FormatSize(TotalBytes);
    public string TimeText => StartTime.ToString("HH:mm:ss");
    public string SpeedText { get => _speedText; private set { _speedText = value; Raise(); } }

    public string StatusText => _state switch
    {
        TransferState.Waiting => "等待中",
        TransferState.Transferring => "传输中",
        TransferState.Done => string.IsNullOrEmpty(_message) ? "已完成" : "已完成 · " + _message,
        TransferState.Failed => string.IsNullOrEmpty(_message) ? "失败" : "失败 · " + _message,
        TransferState.Cancelled => string.IsNullOrEmpty(_message) ? "已取消" : "已取消 · " + _message,
        _ => "",
    };

    public void AddBytes(long n) => Interlocked.Add(ref _transferred, n);
    public void SetBytes(long v) => Interlocked.Exchange(ref _transferred, v);

    public void MarkTransferring()
    {
        _state = TransferState.Transferring;
        _message = null;
        RaiseStatus();
    }

    public void MarkDone(string? filePath = null)
    {
        FilePath = filePath;
        _message = null;
        _state = TransferState.Done;
        SpeedText = "";
        RaiseStatus();
        RaiseProgress();
    }

    public void MarkFailed(string msg)
    {
        _state = TransferState.Failed;
        _message = msg;
        SpeedText = "";
        RaiseStatus();
    }

    public void MarkCancelled(string? msg = null)
    {
        _state = TransferState.Cancelled;
        _message = msg;
        SpeedText = "";
        RaiseStatus();
    }

    public void TickSpeed()
    {
        if (_state != TransferState.Transferring) return;
        var now = Environment.TickCount64;
        var cur = Transferred;
        if (_markTicks == 0)
        {
            _markTicks = now;
            _markBytes = cur;
            return;
        }
        var dt = now - _markTicks;
        if (dt < 400) return;
        var bps = (cur - _markBytes) * 1000.0 / dt;
        SpeedText = bps > 1 ? FormatSize((long)bps) + "/s" : "";
        _markTicks = now;
        _markBytes = cur;
        RaiseProgress();
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} B" : $"{v:0.##} {units[i]}";
    }

    private void RaiseStatus() => Raise(nameof(StatusText));
    private void RaiseProgress()
    {
        Raise(nameof(Percent));
        Raise(nameof(PercentText));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
