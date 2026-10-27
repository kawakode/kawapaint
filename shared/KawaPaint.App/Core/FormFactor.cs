// KawaPaint - which interface to show: the desktop one, or a touch one sized for a tablet or a phone.

using System;

namespace KawaPaint.App.Core;

public enum FormFactor
{
    Desktop,
    Tablet,
    Phone
}

public static class DeviceInfo
{
    /// <summary>
    /// Android's smallest-width breakpoint: below it the screen is a phone's in either
    /// orientation, at or above it a tablet's.
    /// </summary>
    public const double PhoneMaxWidthDp = 600;

    /// <summary>
    /// The screen's shorter side in density-independent pixels, set by a mobile head before the
    /// UI starts. Null on desktops and in the browser.
    /// </summary>
    public static double? SmallestScreenWidthDp { get; set; }

    /// <summary>
    /// The interface for a <see cref="WorkspaceSettings.Interface"/> choice: an explicit
    /// "Desktop", "Tablet" or "Phone", or "Auto" to go by the device.
    /// </summary>
    public static FormFactor Resolve(string? choice)
    {
        if (Enum.TryParse<FormFactor>(choice, ignoreCase: true, out var chosen)) return chosen;
        if (SmallestScreenWidthDp is { } width) return width < PhoneMaxWidthDp ? FormFactor.Phone : FormFactor.Tablet;
        return OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? FormFactor.Phone : FormFactor.Desktop;
    }
}
