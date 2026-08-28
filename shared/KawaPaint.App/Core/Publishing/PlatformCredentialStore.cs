using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KawaPaint.App.Core.Publishing;

public interface ICredentialStore
{
    bool IsPersistent { get; }
    string? Read(string key);
    void Write(string key, string secret);
    void Delete(string key);
}

/// <summary>Uses Windows Credential Manager, macOS Keychain, or Secret Service. Platforms without
/// one of those integrations retain credentials only for the current process.</summary>
public sealed class PlatformCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _session = new(StringComparer.Ordinal);
    public static PlatformCredentialStore Instance { get; } = new();

    public bool IsPersistent => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ||
                                (OperatingSystem.IsLinux() && CommandExists("secret-tool"));

    public string? Read(string key)
    {
        try
        {
            string? value = OperatingSystem.IsWindows() ? WindowsRead(key) :
                OperatingSystem.IsMacOS() ? RunCapture("/usr/bin/security",
                    ["find-generic-password", "-s", "KawaPaint", "-a", key, "-w"]) :
                OperatingSystem.IsLinux() && CommandExists("secret-tool") ? RunCapture("secret-tool",
                    ["lookup", "application", "KawaPaint", "key", key]) : null;
            if (!string.IsNullOrEmpty(value)) return value.TrimEnd('\r', '\n');
        }
        catch { /* unavailable/locked vault falls back to the process session */ }
        return _session.TryGetValue(key, out string? session) ? session : null;
    }

    public void Write(string key, string secret)
    {
        _session[key] = secret;
        try
        {
            if (OperatingSystem.IsWindows()) WindowsWrite(key, secret);
            else if (OperatingSystem.IsMacOS()) RunCapture("/usr/bin/security",
                ["add-generic-password", "-U", "-s", "KawaPaint", "-a", key, "-w", secret]);
            else if (OperatingSystem.IsLinux() && CommandExists("secret-tool"))
                RunCapture("secret-tool", ["store", "--label=KawaPaint", "application", "KawaPaint", "key", key], secret + "\n");
        }
        catch { /* the session copy remains usable */ }
    }

    public void Delete(string key)
    {
        _session.Remove(key);
        try
        {
            if (OperatingSystem.IsWindows()) CredDelete(Target(key), CredentialTypeGeneric, 0);
            else if (OperatingSystem.IsMacOS()) RunCapture("/usr/bin/security",
                ["delete-generic-password", "-s", "KawaPaint", "-a", key]);
            else if (OperatingSystem.IsLinux() && CommandExists("secret-tool"))
                RunCapture("secret-tool", ["clear", "application", "KawaPaint", "key", key]);
        }
        catch { }
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh", ArgumentList = { "-c", "command -v \"$1\" >/dev/null", "sh", command },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            process!.WaitForExit(1000);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? RunCapture(string fileName, IReadOnlyList<string> arguments, string? stdin = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin is not null, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Credential helper did not start.");
        if (stdin is not null) { process.StandardInput.Write(stdin); process.StandardInput.Close(); }
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        return output;
    }

    private static string Target(string key) => "KawaPaint:" + key;
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;

    private static string? WindowsRead(string key)
    {
        if (!CredRead(Target(key), CredentialTypeGeneric, 0, out nint pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0) return string.Empty;
            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    private static void WindowsWrite(string key, string secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > 2560) throw new InvalidOperationException("Credential is too large for Windows Credential Manager.");
        GCHandle pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = Target(key),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = pinned.AddrOfPinnedObject(),
                Persist = CredentialPersistLocalMachine,
                UserName = "KawaPaint"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
