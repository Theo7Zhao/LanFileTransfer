using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TheoTransfer.Core;
using QRCoder;

namespace TheoTransfer;

public partial class MainWindow : Window
{
    private sealed class IpItem
    {
        public required string Ip { get; init; }
        public required string Desc { get; init; }
        public override string ToString() => Desc;
    }

    private readonly AppCore _core;
    private readonly AppSettings _settings;
    private readonly TransferServer _server;
    private readonly ObservableCollection<TransferRecord> _records = new();
    private readonly ObservableCollection<SharedFile> _shared = new();
    private readonly DispatcherTimer _speedTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _currentPort = 8421;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        if (string.IsNullOrWhiteSpace(_settings.StaticKey))
        {
            _settings.StaticKey = AppCore.NewStaticKey();
            _settings.Save();
        }
        _core = new AppCore(_settings.ReceiveFolder, _settings.StaticKey);
        _server = new TransferServer(_core);
        _server.RecordAdded += r => Dispatcher.BeginInvoke(() =>
        {
            _records.Insert(0, r);
            while (_records.Count > 300) _records.RemoveAt(_records.Count - 1);
            EmptyRecords.Visibility = _records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });

        RecordsList.ItemsSource = _records;
        SharedList.ItemsSource = _shared;
        FolderBox.Text = _core.ReceiveFolder;
        if (_settings.Port is >= 1 and <= 65535)
            PortBox.Text = _settings.Port.ToString();
        _core.PairingCodeChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (_hostMode) return;
            CredValue.Text = FormatCode(_core.PairingCode);
            UpdateUrl();
        });
        _core.StaticKeyChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (!_hostMode) return;
            CredValue.Text = FormatKey(_core.StaticKey);
            UpdateUrl();
        });
        SetMode(false);

        _speedTimer.Tick += (_, _) =>
        {
            foreach (var r in _records) r.TickSpeed();
        };
        _speedTimer.Start();

        LoadIps();
        Loaded += async (_, _) => await RestartServerAsync();
    }

    /// <summary>Windows 11 启用系统亚克力背景（窗口透出桌面模糊）；不支持时回退不透明底色。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var backdrop = 3; // DWMBT_TRANSIENTWINDOW（亚克力）
                if (DwmSetWindowAttribute(hwnd, 33, ref backdrop, sizeof(int)) == 0)
                    return;
            }
        }
        catch { }
        RootHost.Background = (Brush)FindResource("FallbackBg");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择接收文件夹（进入目标文件夹后点「打开」）",
            ValidateNames = false,
            CheckFileExists = false,
            FileName = "选择当前文件夹",
        };
        if (dlg.ShowDialog(this) != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        _core.SetReceiveFolder(dir);
        FolderBox.Text = dir;
        _settings.ReceiveFolder = dir;
        _settings.Save();
        StatusText.Text = "接收文件夹已更改为：" + dir;
    }

    private static string FormatCode(string code) => string.Join(" ", code.ToCharArray());
    private static string FormatKey(string key) => string.Join(" ", key.Chunk(2).Select(c => new string(c)));

    private bool _hostMode;

    /// <summary>主客模式切换：客模式=配对码拼入二维码、扫码后一键连接；主模式=静态密钥拼入二维码、扫码自动连接。</summary>
    private void SetMode(bool host)
    {
        _hostMode = host;
        var sel = (Brush)FindResource("ModeSelBrush");
        var sub = (Brush)FindResource("TextSub");
        GuestModeBtn.Background = host ? Brushes.Transparent : sel;
        GuestModeBtn.Foreground = host ? sub : Brushes.White;
        HostModeBtn.Background = host ? sel : Brushes.Transparent;
        HostModeBtn.Foreground = host ? Brushes.White : sub;
        HostModeBtn.BorderBrush = host ? Brushes.Transparent : (Brush)FindResource("CardBorder");
        GuestModeBtn.BorderBrush = host ? (Brush)FindResource("CardBorder") : Brushes.Transparent;

        CredTitle.Text = host ? "静态密钥（已拼入二维码）" : "配对码（已拼入二维码）";
        CredRefreshBtn.Content = host ? "刷新密钥" : "刷新配对码";
        CredValue.Text = host ? FormatKey(_core.StaticKey) : FormatCode(_core.PairingCode);
        CredHint.Text = host
            ? "密钥长期有效，固定设备扫码后自动连接；刷新后旧密钥与已建立的连接立即失效"
            : "扫码后无需输入配对码，点击「连接」即可；连续输错 5 次锁定 30 秒";
        QrHint.Text = host
            ? "主机设备扫码即自动连接，浏览器可收藏该地址长期使用"
            : "访客扫码后点击「连接」即可访问";
        UpdateUrl();
    }

    private void GuestMode_Click(object sender, RoutedEventArgs e) => SetMode(false);

    private void HostMode_Click(object sender, RoutedEventArgs e) => SetMode(true);

    private void RefreshCred_Click(object sender, RoutedEventArgs e)
    {
        if (_hostMode)
        {
            _core.RefreshStaticKey();
            _settings.StaticKey = _core.StaticKey;
            _settings.Save();
            StatusText.Text = "已刷新静态密钥，旧密钥立即失效";
        }
        else
        {
            _core.RefreshPairingCode();
            StatusText.Text = "已刷新配对码，已连接的访客需重新扫码";
        }
    }

    private void LoadIps()
    {
        var items = new List<IpItem>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(a.Address)) continue;
                    items.Add(new IpItem { Ip = a.Address.ToString(), Desc = $"{a.Address}（{nic.Name}）" });
                }
            }
        }
        catch { }

        IpCombo.ItemsSource = items;
        var best = items.FirstOrDefault(i => i.Ip.StartsWith("192.168.")) ?? items.FirstOrDefault();
        IpCombo.SelectedItem = best;
    }

    private const int FallbackPortMin = 49152;
    private const int FallbackPortMax = 65535;
    private const int MaxFallbackAttempts = 50;

    private async Task RestartServerAsync()
    {
        if (!TryGetPort(out var preferred))
        {
            StatusText.Text = "端口无效，请输入 1–65535 之间的数字";
            return;
        }

        StatusText.Text = $"正在启动服务（端口 {preferred}）…";
        try { await _server.StopAsync(); } catch { }

        // 优先级1：用户配置的端口
        if (await TryStartAsync(preferred))
        {
            OnServerStarted(preferred);
            return;
        }

        // 优先级2：在 49152–65535 范围内自动寻找可用端口
        for (var i = 0; i < MaxFallbackAttempts; i++)
        {
            var candidate = Random.Shared.Next(FallbackPortMin, FallbackPortMax + 1);
            if (!IsPortFree(candidate)) continue;
            if (await TryStartAsync(candidate))
            {
                OnServerStarted(candidate);
                ShowToast("端口已自动切换",
                    $"端口 {preferred} 被占用，已自动改用 {candidate}。手机请扫描窗口中的最新二维码或输入新地址。");
                return;
            }
        }

        // 优先级3：所有端口均不可用
        StatusText.Text = "✗ 服务启动失败：无可用端口";
        MessageBox.Show(this,
            "服务启动失败，所有端口均不可用：\n\n" +
            $"· 配置端口 {preferred} 绑定失败\n" +
            $"· 自动搜索范围 {FallbackPortMin}–{FallbackPortMax} 内也未找到可用端口\n\n" +
            "可能原因：防火墙 / 安全软件拦截、系统权限不足。\n\n" +
            "请关闭占用端口的程序后点「重启服务」，或在左侧「端口」框手动输入其他端口（1–65535）后按回车重试。",
            "Theo文件传输 · 启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        PortBox.Focus();
        PortBox.SelectAll();
    }

    private async Task<bool> TryStartAsync(int port)
    {
        try
        {
            await _server.StartAsync(port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnServerStarted(int port)
    {
        _currentPort = port;
        PortBox.Text = port.ToString();
        _settings.Port = port;
        _settings.Save();
        UpdateUrl();
        StatusText.Text = $"● 服务运行中 · 端口 {port} · 等待手机连接";
    }

    private DispatcherTimer? _toastTimer;

    /// <summary>非阻断式通知：自动 8 秒后消失，点击立即关闭。</summary>
    private void ShowToast(string title, string message, int seconds = 8)
    {
        ToastTitle.Text = title;
        ToastMessage.Text = message;
        ToastCard.Visibility = Visibility.Visible;

        _toastTimer ??= new DispatcherTimer();
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Tick -= ToastTimer_Tick;
        _toastTimer.Tick += ToastTimer_Tick;
        _toastTimer.Start();
    }

    private void ToastTimer_Tick(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        ToastCard.Visibility = Visibility.Collapsed;
    }

    private void ToastCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _toastTimer?.Stop();
        ToastCard.Visibility = Visibility.Collapsed;
    }

    private bool TryGetPort(out int port) =>
        int.TryParse(PortBox.Text.Trim(), out port) && port is >= 1 and <= 65535;

    private void UpdateUrl()
    {
        var ip = IpCombo.SelectedItem as IpItem;
        if (ip == null)
        {
            UrlText.Text = "未检测到局域网 IPv4 地址";
            QrImage.Source = null;
            return;
        }
        var url = _hostMode
            ? $"http://{ip.Ip}:{_currentPort}/?key={_core.StaticKey}"
            : $"http://{ip.Ip}:{_currentPort}/?code={_core.PairingCode}";
        UrlText.Text = url;
        QrImage.Source = MakeQr(url);
    }

    private static BitmapImage? MakeQr(string text)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(9);
            var img = new BitmapImage();
            using var ms = new MemoryStream(png);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private void IpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUrl();

    private async void PortBox_Changed(object sender, RoutedEventArgs e)
    {
        if (TryGetPort(out var port) && port != _currentPort)
            await RestartServerAsync();
    }

    private async void PortBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (TryGetPort(out var port) && port != _currentPort)
                await RestartServerAsync();
        }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e) => await RestartServerAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _core.EnsureFolders();
            Process.Start(new ProcessStartInfo(_core.ReceiveFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"无法打开接收文件夹：\n{_core.ReceiveFolder}\n\n{ex.Message}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddShare_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要共享给手机的文件" };
        if (dlg.ShowDialog(this) == true)
            AddShared(dlg.FileNames.ToArray());
    }

    private void ClearShares_Click(object sender, RoutedEventArgs e)
    {
        if (_shared.Count == 0) return;
        if (MessageBox.Show(this, "确定清空共享列表吗？（不会删除电脑上的文件）", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var f in _shared.ToList()) _core.RemoveShare(f.Id);
        _shared.Clear();
        StatusText.Text = "已清空共享列表";
    }

    private void RemoveShare_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Guid id) return;
        if (!_core.RemoveShare(id)) return;
        var item = _shared.FirstOrDefault(f => f.Id == id);
        if (item != null) _shared.Remove(item);
    }

    private void AddShared(string[] paths)
    {
        var added = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p))
                {
                    _shared.Insert(0, _core.AddShare(p));
                    added++;
                }
                else if (Directory.Exists(p))
                {
                    foreach (var f in Directory.GetFiles(p))
                    {
                        _shared.Insert(0, _core.AddShare(f));
                        added++;
                    }
                }
            }
            catch { }
        }
        if (added > 0)
            StatusText.Text = $"已共享 {added} 个文件，手机端可下载";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            AddShared(paths);
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _speedTimer.Stop();
        _ = _server.StopAsync();
    }
}
