using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashDraft.Core;

namespace FlashDraft.App;

/// <summary>
/// Main window state. Transport writes immutable progress snapshots into a
/// bounded channel; this ViewModel drains it and marshals every update onto the
/// UI thread. Cancellation is propagated with CancellationToken.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private string _connectionCode = string.Empty;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _speedText = "0 B/s";
    [ObservableProperty] private string _statusText = "Idle";

    private readonly Channel<ProgressSnapshot> _progressChannel;
    private CancellationTokenSource? _cancellation;

    public MainViewModel()
    {
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

    public string AboutText => AboutNotice.Text;

    public CancellationToken TransferToken => (_cancellation ??= new CancellationTokenSource()).Token;

    [RelayCommand]
    private void GenerateCode()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Adjustable: the real ephemeral X25519 public key comes from the Crypto
        // layer; random bytes here keep the scaffold dependency-free.
        var session = new TransferSession(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            now,
            now.AddMinutes(5),
            RandomNumberGenerator.GetBytes(ProtocolConstants.TokenSize),
            Array.Empty<string>(),
            null);

        ConnectionCode = FlashDraft.Core.ConnectionCode.Encode(session);
        StatusText = "Code generated. Share it out of band.";
    }

    [RelayCommand]
    private void Start()
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        StatusText = "Transferring";
        // Adjustable: wire LanTcpTransferService / UdpHolePunchingService here
        // once the Transport package is referenced by the client project.
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellation?.Cancel();
        StatusText = "Cancelled";
    }

    private async Task DrainProgressAsync()
    {
        await foreach (ProgressSnapshot snapshot in _progressChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Adjustable: UiRefreshMs throttling can be added here if the UI
            // receives more snapshots than it can paint comfortably.
            await Dispatcher.UIThread.InvokeAsync(() => Apply(snapshot)).ConfigureAwait(false);
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
    public const string Text =
        "FlashDraft's user interface design is inspired by Plain Craft Launcher (PCL) by 龙腾猫跃 (Meloong-Git/PCL). This project contains no PCL source code, images, logos, or fonts. FlashDraft is an independent file-transfer tool and is not affiliated with, endorsed by, or officially related to PCL or its author. PCL and all related rights remain with the original author. See https://github.com/Meloong-Git/PCL/blob/main/LICENCE.\n\n" +
        "FlashDraft 的界面设计受龙腾猫跃的 Plain Craft Launcher (PCL) 启发。本项目不包含 PCL 的源代码、图片、Logo 或字体。FlashDraft 是独立的文件传输工具，与 PCL 及其作者无关联、非官方作品、未获其背书。PCL 及相关权利归原作者所有。参见 https://github.com/Meloong-Git/PCL/blob/main/LICENCE。";
}
