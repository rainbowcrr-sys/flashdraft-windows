using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlashDraft.App;

/// <summary>
/// Shared content view. Desktop hosts it inside a Window; Android hosts it as
/// the single application view. Keeping the same control avoids a blank screen
/// or an immediate exit on platforms that use ISingleViewApplicationLifetime.
/// </summary>
public partial class MainView : UserControl
{
    public MainView() => AvaloniaXamlLoader.Load(this);
}
