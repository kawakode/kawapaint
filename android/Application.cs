using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace KawaPaint.Android;

[Application]
public sealed class Application : AvaloniaAndroidApplication<KawaPaint.App.App>
{
    public Application(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        // Before Avalonia starts, so MainView can pick the phone or tablet interface from it.
        if (Resources?.Configuration is { } configuration)
            KawaPaint.App.Core.DeviceInfo.SmallestScreenWidthDp = configuration.SmallestScreenWidthDp;
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
