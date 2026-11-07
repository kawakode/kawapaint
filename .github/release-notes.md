## Downloads

| Platform | File |
| --- | --- |
| macOS (Apple Silicon) | `KawaPaint-<version>-osx-arm64.dmg` |
| Windows (x64) | `KawaPaint-<version>-win-x64.zip` |
| Linux (x64) | `KawaPaint-<version>-linux-x64.zip` |

The builds are self-contained: no .NET runtime is needed.

### macOS: first launch

Open the `.dmg` and drag **KawaPaint** onto the **Applications** folder.

KawaPaint is not yet signed with an Apple Developer ID nor notarized, so macOS blocks the first
launch with a message saying Apple could not verify the app. To allow it once:

1. Open KawaPaint from Applications and close the warning (click **Done**, not "Move to Trash").
2. Open **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to
   the KawaPaint message, then confirm with your password.

Alternatively, in Terminal: `xattr -dr com.apple.quarantine /Applications/KawaPaint.app`

### Windows

Windows SmartScreen may warn about an unknown publisher: click **More info → Run anyway**.
The Microsoft Store version does not show this warning.

