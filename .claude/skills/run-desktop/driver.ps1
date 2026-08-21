# KawaPaint desktop UI driver — Windows PowerShell 5.1, no extra packages.
#
# Avalonia renders its own menus/dialogs (Skia canvas, not native HWND controls), so there is no
# UI-Automation tree or native menu API to query. Every interaction here is a real pixel click or
# real keystroke, and every "did it work" check is a screenshot you actually look at (Read tool on
# the PNG). This is slower than a proper automation framework but needs nothing installed.
#
# Usage: dot-source this file, then call the functions.
#   . .claude\skills\run-desktop\driver.ps1
#   $p = Start-KawaPaint
#   Get-Screenshot -Path C:\...\01.png
#   # Read the PNG, find where to click, then:
#   Send-Click -X 31 -Y 48
#   ...
#   Stop-KawaPaint

Add-Type @"
using System;
using System.Runtime.InteropServices;

public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern short VkKeyScan(char ch);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public struct RECT { public int Left, Top, Right, Bottom; }

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const int SW_RESTORE = 9;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_TAB = 0x09;

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    // Ctrl+click, for multi-select in native Explorer-style list views (Save/Open dialogs).
    // Typing several quoted paths into the filename field is NOT a reliable substitute — it has
    // been observed to silently keep only the last path. Ctrl+click the actual icons instead.
    public static void CtrlClick(int x, int y) {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        Click(x, y);
        System.Threading.Thread.Sleep(40);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void KeyPress(byte vk) {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void TypeText(string s) {
        foreach (char c in s) {
            short vk = VkKeyScan(c);
            byte b = (byte)(vk & 0xff);
            bool shift = (vk & 0x100) != 0;
            if (shift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
            keybd_event(b, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(15);
            keybd_event(b, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (shift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
"@

function Get-Screenshot {
    <#
    .SYNOPSIS
    Captures a screen region to PNG. Defaults to a 1920x1080 full-screen capture, because native
    Save/Open/folder dialogs are separate top-level windows, not children of the app window - a
    screenshot cropped to the app's bounds silently misses them.
    #>
    param([Parameter(Mandatory)][string]$Path, [int]$X = 0, [int]$Y = 0, [int]$Width = 1920, [int]$Height = 1080)
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $Width, $Height))
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Send-Click { param([int]$X, [int]$Y) [Win32]::Click($X, $Y) }
function Send-CtrlClick { param([int]$X, [int]$Y) [Win32]::CtrlClick($X, $Y) }
function Send-Text { param([string]$Text) [Win32]::TypeText($Text) }
function Send-Enter { [Win32]::KeyPress([Win32]::VK_RETURN) }
function Send-Escape { [Win32]::KeyPress([Win32]::VK_ESCAPE) }

function Start-KawaPaint {
    <#
    .SYNOPSIS
    Launches KawaPaint.Win.exe, waits for its window, pins it to a fixed position/size (so
    coordinates you compute from one screenshot stay valid across the whole session), and brings
    it to the foreground. Returns the Process object.
    .PARAMETER ExePath
    Defaults to the Debug build output. Build first if it's missing:
    & "C:\Program Files\dotnet\dotnet.exe" build win\KawaPaint.Win.csproj
    (dotnet is often not on PATH in agent shells in this environment - use the full path.)
    #>
    param(
        [string]$ExePath = "C:\Users\Kawa\kawapaint\win\bin\Debug\net10.0\KawaPaint.Win.exe",
        [int]$X = 0, [int]$Y = 0, [int]$Width = 1280, [int]$Height = 900
    )
    Start-Process $ExePath
    $p = $null
    for ($i = 0; $i -lt 20 -and -not $p; $i++) {
        Start-Sleep -Milliseconds 500
        $p = Get-Process -Name "KawaPaint.Win" -ErrorAction SilentlyContinue |
             Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    if (-not $p) { throw "KawaPaint window did not appear within 10s." }

    [Win32]::ShowWindow($p.MainWindowHandle, [Win32]::SW_RESTORE) | Out-Null
    [Win32]::MoveWindow($p.MainWindowHandle, $X, $Y, $Width, $Height, $true) | Out-Null
    [Win32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 500
    return $p
}

function Stop-KawaPaint {
    Stop-Process -Name "KawaPaint.Win" -Force -ErrorAction SilentlyContinue
}
