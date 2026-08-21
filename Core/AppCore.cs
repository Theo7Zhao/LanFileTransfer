using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TheoTransfer.Core;

public sealed class AppCore
{
    public const int ChunkSize = 4 * 1024 * 1024;
    private static readonly TimeSpan SessionLife = TimeSpan.FromHours(12);
    private static readonly TimeSpan UploadIdleLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PairLockTime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();
    private readonly ConcurrentDictionary<string, PairLimit> _pairLimits = new();
    private string _pairingCode = NewCode();
    private string _staticKey;

    public event Action? PairingCodeChanged;
    public event Action? StaticKeyChanged;

    public string PairingCode => _pairingCode;
    public string StaticKey => _staticKey;
    public string ReceiveFolder { get; private set; }
    public string TempFolder => Path.Combine(ReceiveFolder, ".partial");
    public ConcurrentDictionary<string, SharedFile> Outbox { get; } = new();
    public ConcurrentDictionary<string, UploadSession> Uploads { get; } = new();

    public AppCore(string? receiveFolder = null, string? staticKey = null)
    {
        var folder = receiveFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
                profile = Path.GetDirectoryName(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                    ?? AppContext.BaseDirectory;
            folder = Path.Combine(profile, "TheoTransfer");
        }
        ReceiveFolder = folder;
        _staticKey = string.IsNullOrWhiteSpace(staticKey) ? NewStaticKey() : staticKey;
        EnsureFolders();
    }

    /// <summary>确保接收目录和临时目录存在（用户可能删除过，运行期自动重建）。</summary>
    public void EnsureFolders()
    {
        Directory.CreateDirectory(TempFolder);
    }

    public void SetReceiveFolder(string folder)
    {
        ReceiveFolder = folder;
        EnsureFolders();
    }

    public void RefreshPairingCode()
    {
        _pairingCode = NewCode();
        _sessions.Clear();
        PairingCodeChanged?.Invoke();
    }

    /// <summary>刷新静态密钥：旧密钥立即失效，所有已连接会话断开（需重新扫码）。</summary>
    public string RefreshStaticKey()
    {
        _staticKey = NewStaticKey();
        _sessions.Clear();
        StaticKeyChanged?.Invoke();
        return _staticKey;
    }

    public bool IsPairLocked(string ip) =>
        _pairLimits.TryGetValue(ip, out var l) && l.LockedUntil > DateTime.UtcNow;

    /// <summary>验证配对码（客模式）或静态密钥（主模式），成功返回新会话令牌；失败计入锁定计数。</summary>
    public string? TryPair(string ip, string code, string? key)
    {
        var limit = _pairLimits.GetOrAdd(ip, _ => new PairLimit());
        lock (limit)
        {
            if (limit.LockedUntil > DateTime.UtcNow) return null;
            var okKey = !string.IsNullOrEmpty(key) && FixedTimeEquals(key, _staticKey);
            var okCode = FixedTimeEquals(code, _pairingCode);
            if (!okKey && !okCode)
            {
                limit.Fails++;
                if (limit.Fails >= 5)
                {
                    limit.Fails = 0;
                    limit.LockedUntil = DateTime.UtcNow + PairLockTime;
                }
                return null;
            }
            limit.Fails = 0;
            limit.LockedUntil = DateTime.MinValue;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = DateTime.UtcNow + SessionLife;
        return token;
    }

    public bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (_sessions.TryGetValue(token, out var exp))
        {
            if (exp > DateTime.UtcNow)
            {
                _sessions[token] = DateTime.UtcNow + SessionLife;
                return true;
            }
            _sessions.TryRemove(token, out _);
        }
        return false;
    }

    public UploadSession CreateUpload(string name, long size)
    {
        EnsureFolders();
        var s = new UploadSession
        {
            FileName = name,
            TotalSize = size,
            TempPath = Path.Combine(TempFolder, Guid.NewGuid().ToString("N") + ".part"),
        };
        s.Stream = new FileStream(s.TempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        Uploads[s.Id] = s;
        return s;
    }

    public string CompleteUpload(UploadSession s)
    {
        EnsureFolders();
        var final = UniquePath(ReceiveFolder, s.FileName);
        File.Move(s.TempPath, final);
        Uploads.TryRemove(s.Id, out _);
        return final;
    }

    public void CancelUpload(string id, string reason)
    {
        if (!Uploads.TryRemove(id, out var s)) return;
        try { s.Dispose(); } catch { }
        TryDelete(s.TempPath);
        s.Record?.MarkCancelled(reason);
    }

    public SharedFile AddShare(string path)
    {
        var f = new SharedFile
        {
            Name = System.IO.Path.GetFileName(path),
            Path = path,
            Size = new FileInfo(path).Length,
        };
        Outbox[f.Id.ToString()] = f;
        return f;
    }

    public bool RemoveShare(Guid id) => Outbox.TryRemove(id.ToString(), out _);

    public void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (token, exp) in _sessions)
            if (exp <= now) _sessions.TryRemove(token, out _);

        foreach (var (id, s) in Uploads)
        {
            if (now - s.LastActive <= UploadIdleLimit) continue;
            if (!Uploads.TryRemove(id, out var dead)) continue;
            try { dead.Dispose(); } catch { }
            TryDelete(dead.TempPath);
            dead.Record?.MarkFailed("传输超时中断");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string UniquePath(string dir, string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var p = Path.Combine(dir, name);
        var i = 1;
        while (File.Exists(p)) p = Path.Combine(dir, $"{baseName} ({i++}){ext}");
        return p;
    }

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>8 位静态密钥：去掉易混淆字符（0/O、1/I 等），便于人工核对与拼接。</summary>
    public static string NewStaticKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 8)
            .Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private sealed class PairLimit
    {
        public int Fails;
        public DateTime LockedUntil = DateTime.MinValue;
    }
}
