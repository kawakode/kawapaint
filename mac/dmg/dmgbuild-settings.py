# dmgbuild settings for the KawaPaint drag-to-Applications disk image.
# Used by build-dmg.sh as: dmgbuild -s dmgbuild-settings.py -D app=... -D background=... -D icon=... "KawaPaint" out.dmg
# dmgbuild writes the Finder layout (.DS_Store) directly, so no Finder/AppleScript session is
# needed on the CI runner. dmgbuild picks up background@2x.png next to background.png and
# combines both into a Retina TIFF. Keep the icon positions in sync with make-assets.py.
import os.path

app = defines["app"]  # noqa: F821 - injected by dmgbuild
app_name = os.path.basename(app)

format = "UDZO"
filesystem = "HFS+"
files = [app]
symlinks = {"Applications": "/Applications"}
icon = defines["icon"]  # noqa: F821
badge_icon = None

background = defines["background"]  # noqa: F821
window_rect = ((200, 120), (660, 400))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
show_icon_preview = False

icon_size = 128
text_size = 13
arrange_by = None
icon_locations = {
    app_name: (170, 190),
    "Applications": (490, 190),
}
# No hide_extensions: dmgbuild implements it with a com.apple.FinderInfo xattr on the bundle,
# which breaks `codesign --verify --strict` (and so Gatekeeper's check) on the copied app.
# Finder already hides the ".app" extension by default.
