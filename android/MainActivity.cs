using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Webkit;
using Android.Widget;
using Avalonia.Android;
using AndroidUri = Android.Net.Uri;

namespace KawaPaint.Android;

// SingleTask: an image sent while KawaPaint is already open arrives in OnNewIntent of the one
// activity, instead of starting a second copy of the editor inside the sending app's task.
[Activity(
    Label = "KawaPaint",
    Theme = "@style/KawaPaintTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
// "Open with" from a file manager or gallery, and "Share" from any app, for images.
[IntentFilter(new[] { Intent.ActionView, Intent.ActionEdit },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "image/*")]
[IntentFilter(new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "image/*")]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const string ImportFolder = "incoming";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Back with unsaved changes asks first (MainView.OnBackRequested), then quits through this.
        KawaPaint.App.App.QuitApp = Finish;
        base.OnCreate(savedInstanceState);

        // Only on a fresh start: when the system rebuilds the activity it hands over the same
        // intent again, and the image must not be opened a second time over the user's work.
        if (savedInstanceState is null)
        {
            ClearImportFolder();
            ImportFrom(Intent);
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ImportFrom(intent);
    }

    /// <summary>
    /// Opens the image an "Open with" or "Share" intent points at. The sender only lends read
    /// access to its content URI, so the image is copied into the app's cache and the shared code
    /// opens that copy as an untitled image.
    /// </summary>
    private void ImportFrom(Intent? intent)
    {
        if (intent is null) return;
        AndroidUri? uri = intent.Action switch
        {
            Intent.ActionView or Intent.ActionEdit => intent.Data,
            Intent.ActionSend => SharedStream(intent),
            _ => null,
        };
        if (uri is null) return;

        Task.Run(() => CopyToCache(uri)).ContinueWith(copy => RunOnUiThread(() =>
        {
            if (copy.IsCompletedSuccessfully)
            {
                KawaPaint.App.App.ImportFile(copy.Result);
            }
            else
            {
                string reason = copy.Exception?.GetBaseException().Message ?? "unknown error";
                Toast.MakeText(this, "KawaPaint could not open the image: " + reason, ToastLength.Long)?.Show();
            }
        }));
    }

    private static AndroidUri? SharedStream(Intent intent)
    {
#pragma warning disable CA1422, CS0618 // the typed overload only exists from API 33
        return intent.GetParcelableExtra(Intent.ExtraStream) as AndroidUri;
#pragma warning restore CA1422, CS0618
    }

    private string CopyToCache(AndroidUri uri)
    {
        string name = Path.GetFileName(DisplayName(uri) ?? uri.LastPathSegment ?? "image");
        if (string.IsNullOrWhiteSpace(name)) name = "image";
        if (!Path.HasExtension(name))
        {
            // The decoder is picked by extension; content URIs often carry only a MIME type.
            string? mime = ContentResolver?.GetType(uri);
            string? extension = mime is null ? null : MimeTypeMap.Singleton?.GetExtensionFromMimeType(mime);
            if (!string.IsNullOrEmpty(extension)) name += "." + extension;
        }

        string folder = Path.Combine(CacheDir!.AbsolutePath, ImportFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        using Stream input = ContentResolver?.OpenInputStream(uri)
            ?? throw new IOException("The image is no longer available.");
        using FileStream output = File.Create(path);
        input.CopyTo(output);
        return path;
    }

    private string? DisplayName(AndroidUri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(uri, new[] { "_display_name" }, null, null, null);
            if (cursor is not null && cursor.MoveToFirst() && !cursor.IsNull(0))
                return cursor.GetString(0);
        }
        catch { /* not every provider answers this; the URI's last segment is used instead */ }
        return null;
    }

    private void ClearImportFolder()
    {
        try
        {
            string folder = Path.Combine(CacheDir!.AbsolutePath, ImportFolder);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch { /* leftovers are only cache */ }
    }
}
