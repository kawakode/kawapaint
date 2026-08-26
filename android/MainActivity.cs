using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace KawaPaint.Android;

[Activity(
    Label = "KawaPaint",
    Theme = "@style/KawaPaintTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
