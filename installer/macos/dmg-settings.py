# dmgbuild settings for the slaw disk image. build-dmg.sh passes:
#   -D app=<path to slaw.app>  -D background=<path to background.png>
# dmgbuild picks up background@2x.png next to it for Retina windows.
# Icon positions and the window size must agree with render-assets.swift.

import os.path

application = defines["app"]  # noqa: F821
appname = os.path.basename(application)

format = "UDZO"
files = [application]
symlinks = {"Applications": "/Applications"}
hide_extension = [appname]

icon = os.path.join(application, "Contents", "Resources", "slaw.icns")
background = defines["background"]  # noqa: F821

# Outer frame: 420 points of content plus the title bar
window_rect = ((200, 120), (660, 453))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
show_icon_preview = False

icon_size = 96
text_size = 12
label_pos = "bottom"
icon_locations = {
    appname: (170, 205),
    "Applications": (500, 205),
}
