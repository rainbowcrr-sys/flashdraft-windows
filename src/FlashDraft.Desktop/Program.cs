using Avalonia;
using FlashDraft.App;

namespace FlashDraft.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::FlashDraft.App.App>().UsePlatformDetect().LogToTrace();
}
