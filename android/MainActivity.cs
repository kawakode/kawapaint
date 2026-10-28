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
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        // Back with unsaved changes asks first (MainView.OnBackRequested), then quits through this.
        KawaPaint.App.App.QuitApp = Finish;
        base.OnCreate(savedInstanceState);
    }
}
