using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashDraft.Core;
using FlashDraft.Crypto;
using FlashDraft.Transport;

namespace FlashDraft.App;

/// <summary>
/// Main window state. Transfer writes immutable progress snapshots into a
/// bounded channel; this model drains it on the UI thread. Cancellation is
/// propagated with CancellationToken.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private string _localCode = string.Empty;
    [ObservableProperty] private string _remoteCode = string.Empty;
    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _destinationPath = string.Empty;
    [ObservableProperty] private bool _isWanMode;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _speedText = "0 B/s";
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private bool _licensePending = !LicenseGate.IsAccepted();
    [ObservableProperty] private bool _mainVisible = LicenseGate.IsAccepted();
    [ObservableProperty] private bool _licenseAccepted;

    private readonly SessionOrchestrator _orchestrator = new();
    private readonly Channel<ProgressSnapshot> _progressChannel;
    private CancellationTokenSource? _cancellation;
    private SessionOffer? _localOffer;
    private SessionOffer? _remoteOffer;
    private SessionKeys? _keys;
    private Guid _sessionId;

    public MainViewModel()
    {
        Loc.LanguageChanged += () => { OnPropertyChanged(nameof(AboutText)); OnPropertyChanged(nameof(IsChinese)); OnPropertyChanged(nameof(IsEnglish)); };
        _progressChannel = Channel.CreateBounded<ProgressSnapshot>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        Progress = new ChannelProgress(_progressChannel.Writer);
        _ = DrainProgressAsync();
    }

    public ObservableCollection<string> Devices { get; } = new();

    public IProgress<ProgressSnapshot> Progress { get; }

    /// <summary>Standalone About page: short notice plus the full license.</summary>

    public bool IsChinese => Loc.IsChinese;
    public bool IsEnglish => !Loc.IsChinese;

    [RelayCommand]
    private void SetLanguage(string lang)
    {
        Loc.Set(lang);
        OnPropertyChanged(nameof(AboutText));
        OnPropertyChanged(nameof(LicenseText));
        OnPropertyChanged(nameof(IsChinese));
        OnPropertyChanged(nameof(IsEnglish));
    }

    public string AboutText => LicenseContent.Notice;

    public string LicenseText => LicenseContent.Text;

    [RelayCommand]
    private void AcceptLicense()
    {
        if (!LicenseAccepted) return;
        LicenseGate.MarkAccepted();
        LicensePending = false;
        MainVisible = true;
    }

    [RelayCommand]
    private async Task GenerateCode()
    {
        SetStatus("Generating offer");
        try
        {
            _localOffer = await _orchestrator.CreateOfferAsync(CancellationToken.None);
            LocalCode = _orchestrator.Encode(_localOffer);
            SetStatus("Offer ready; share it out of band");
        }
        catch (Exception ex)
        {
            SetStatus("Offer failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private void BindPeer()
    {
        if (_localOffer is null)
        {
            SetStatus("Generate your own offer first");
            return;
        }

        try
        {
            _remoteOffer = _orchestrator.Parse(RemoteCode, DateTimeOffset.UtcNow);
            (_keys, _sessionId) = _orchestrator.Bind(_localOffer, _remoteOffer);
            SetStatus("Session bound; ready to transfer");
        }
        catch (Exception ex)
        {
            SetStatus("Bind failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task WaitForTransfer()
    {
        if (_localOffer is null || _keys is null)
        {
            SetStatus("Generate and bind first");
            return;
        }

        string directory = string.IsNullOrWhiteSpace(DestinationPath)
            ? Path.Combine(Path.GetTempPath(), "FlashDraftReceived")
            : DestinationPath;
        try
        {
            Directory.CreateDirectory(directory);
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            SetStatus("Waiting for peer");
            await _orchestrator.ReceiveAsync(
                _localOffer, _sessionId, _keys, directory,
                Progress, IsWanMode, _cancellation.Token);
            SetStatus("Transfer completed");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            SetStatus("Receive failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task SendFile()
    {
        if (_remoteOffer is null || _keys is null)
        {
            SetStatus("Bind a peer offer first");
            return;
        }

        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath))
        {
            SetStatus("Set a valid source file first");
            return;
        }

        try
        {
            int chunk = IsWanMode ? ProtocolConstants.WanChunkSize : ProtocolConstants.ChunkSize;
            FileDescriptor descriptor = Describe(SourcePath, chunk);
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            SetStatus("Sending");
            await _orchestrator.SendAsync(
                _remoteOffer, _sessionId, _keys,
                new[] { descriptor }, new[] { SourcePath },
                Progress, IsWanMode, _cancellation.Token);
            SetStatus("Transfer completed");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            SetStatus("Send failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Single-device self test: two in-process peers exchange offers and really
    /// transfer one generated file over loopback TCP. No second device needed.
    /// </summary>
    [RelayCommand]
    private async Task RunLoopbackTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "fd-self-" + Guid.NewGuid().ToString("N"));
        string src = Path.Combine(root, "src");
        string dst = Path.Combine(root, "dst");
        try
        {
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            byte[] content = RandomNumberGenerator.GetBytes(ProtocolConstants.ChunkSize * 2 + 137);
            string sourcePath = Path.Combine(src, "sample.bin");
            File.WriteAllBytes(sourcePath, content);

            int port = ReserveTcpPort();
            var a = new SessionOrchestrator(new NullStunClient(), lanPort: port);
            var b = new SessionOrchestrator(new NullStunClient(), lanPort: port);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var offerA = new SessionOffer(Guid.NewGuid(), a.LocalPublicKey, now.AddMinutes(5),
                RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
                new[] { "127.0.0.1:" + port }, null);
            var offerB = new SessionOffer(Guid.NewGuid(), b.LocalPublicKey, now.AddMinutes(5),
                RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
                Array.Empty<string>(), null);
            var (keysA, sidA) = a.Bind(offerA, offerB);
            var (keysB, sidB) = b.Bind(offerB, offerA);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            FileDescriptor descriptor = Describe(sourcePath, ProtocolConstants.ChunkSize);
            Task receive = a.ReceiveAsync(offerA, sidA, keysA, dst, Progress, false, cts.Token);
            await Task.Delay(150, cts.Token);
            Task send = b.SendAsync(offerA, sidB, keysB,
                new[] { descriptor }, new[] { sourcePath }, Progress, false, cts.Token);
            await Task.WhenAll(receive, send).WaitAsync(TimeSpan.FromSeconds(30));

            string target = Path.Combine(dst, "sample.bin");
            bool ok = File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(content);
            SetStatus(ok ? "Loopback transfer OK" : "Loopback transfer FAILED");
        }
        catch (Exception ex)
        {
            SetStatus("Loopback test failed: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellation?.Cancel();
        SetStatus("Cancelled");
    }

    private static FileDescriptor Describe(string path, int chunkSize)
    {
        byte[] hash;
        using (FileStream stream = File.OpenRead(path))
        {
            hash = SHA256.HashData(stream);
        }

        long length = new FileInfo(path).Length;
        uint total = (uint)((length + chunkSize - 1) / chunkSize);
        return new FileDescriptor(0, Path.GetFileName(path), length, total, hash)
        {
            ChunkSize = chunkSize,
        };
    }

    private static int ReserveTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private void SetStatus(string text) =>
        Dispatcher.UIThread.Post(() => StatusText = text);

    private async Task DrainProgressAsync()
    {
        await foreach (ProgressSnapshot snapshot in _progressChannel.Reader.ReadAllAsync())
        {
            await Dispatcher.UIThread.InvokeAsync(() => Apply(snapshot));
        }
    }

    private void Apply(ProgressSnapshot snapshot)
    {
        ProgressPercent = snapshot.TotalBytes == 0
            ? 0
            : 100.0 * snapshot.BytesTransferred / snapshot.TotalBytes;
        SpeedText = $"{snapshot.BytesPerSecond / 1024.0 / 1024.0:F2} MiB/s";
        StatusText = snapshot.State.ToString();
    }

    private sealed class ChannelProgress : IProgress<ProgressSnapshot>
    {
        private readonly ChannelWriter<ProgressSnapshot> _writer;

        public ChannelProgress(ChannelWriter<ProgressSnapshot> writer) => _writer = writer;

        public void Report(ProgressSnapshot value) => _writer.TryWrite(value);
    }
}

/// <summary>
/// Identical notice text is used in README, the About view, and LICENSE of
/// every repository. Keep the three copies in sync.
/// </summary>
public static class AboutNotice
{
    /// <summary>Follows the language selected in Settings / by the installer.</summary>
    public static string Text => Loc.Notice();
}
