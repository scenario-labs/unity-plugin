"""Renders the plugin's UI icons and logo to PNG.

Icons are drawn on a 24 px grid as white strokes; the window tints them with
USS (-unity-background-image-tint-color), so one file serves both editor themes.
The Scenario logo comes from Scenario's own black and white assets.

    uv run --with resvg-py tools/build_icons.py
"""
import os
import resvg_py

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Packages", "com.scenario.unity", "Editor", "UI", "Icons")
LOGO_SRC = os.path.join(ROOT, "tools", "logo")
SIZE = 48

HEAD = '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">'
FILL = '<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="#fff" stroke="none">'

ICONS = {
    # navigation
    "create": '<path d="M12 3.5l1.9 5.1 5.1 1.9-5.1 1.9L12 17.5l-1.9-5.1L5 10.5l5.1-1.9z"/><path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z"/>',
    "library": '<rect x="3.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.5"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.5"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.5"/>',
    "jobs": '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
    "workflows": '<rect x="3" y="4" width="6" height="5" rx="1.2"/><rect x="15" y="15" width="6" height="5" rx="1.2"/><rect x="15" y="4" width="6" height="5" rx="1.2"/><path d="M9 6.5h6M18 9v6M9 6.5c3 0 3 11 6 11"/>',
    "agent": '<rect x="4.5" y="7.5" width="15" height="11" rx="3"/><path d="M12 7.5V4.5"/><circle cx="12" cy="3.6" r=".9"/><circle cx="9.3" cy="13" r="1.1"/><circle cx="14.7" cy="13" r="1.1"/><path d="M2.5 12v3M21.5 12v3"/>',
    "settings": '<path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2.2"/><circle cx="9" cy="17" r="2.2"/>',
    # output types
    "image": '<rect x="3.5" y="4.5" width="17" height="15" rx="2"/><circle cx="9" cy="9.5" r="1.6"/><path d="M4 17l5-5 4 4 2.5-2.5L20 18"/>',
    "video": '<rect x="3.5" y="5.5" width="17" height="13" rx="2"/><path d="M10.5 9.3v5.4l4.5-2.7z"/>',
    "cube": '<path d="M12 3l8 4.5v9L12 21l-8-4.5v-9z"/><path d="M4 7.5l8 4.5 8-4.5M12 12v9"/>',
    "audio": '<path d="M4 10v4M8 7v10M12 4v16M16 8v8M20 10.5v3"/>',
    "material": '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5c-3 3-3 14 0 17M12 3.5c3 3 3 14 0 17"/>',
    "skybox": '<path d="M2.5 16.5c3-4 5.5-6 8-6s4 2 6 2 3.5-1 5-2.5"/><path d="M2.5 20.5h19"/><circle cx="17" cy="6" r="2.3"/>',
    "motion": '<circle cx="13.5" cy="4.5" r="1.8"/><path d="M8 21l3-6 2.5 2V22M11 15l1.5-6.5L9 10l-2.5 3M12.5 8.5l2.5 3 3.5 1"/>',
    "grid": '<circle cx="6" cy="6" r="1.4"/><circle cx="12" cy="6" r="1.4"/><circle cx="18" cy="6" r="1.4"/><circle cx="6" cy="12" r="1.4"/><circle cx="12" cy="12" r="1.4"/><circle cx="18" cy="12" r="1.4"/><circle cx="6" cy="18" r="1.4"/><circle cx="12" cy="18" r="1.4"/><circle cx="18" cy="18" r="1.4"/>',
    # actions
    "search": '<circle cx="10.5" cy="10.5" r="6.5"/><path d="M15.5 15.5L20.5 20.5"/>',
    "star": '<path d="M12 3.6l2.6 5.3 5.8.8-4.2 4.1 1 5.8L12 16.9l-5.2 2.7 1-5.8-4.2-4.1 5.8-.8z"/>',
    "close": '<path d="M6 6l12 12M18 6L6 18"/>',
    "check": '<path d="M4.5 12.5l5 5 10-11"/>',
    "chevron-down": '<path d="M6 9.5l6 6 6-6"/>',
    "chevron-right": '<path d="M9.5 6l6 6-6 6"/>',
    "chevron-left": '<path d="M14.5 6l-6 6 6 6"/>',
    "plus": '<path d="M12 5v14M5 12h14"/>',
    "upload": '<path d="M12 15V4M7.5 8.5L12 4l4.5 4.5"/><path d="M4 15v3.5A1.5 1.5 0 005.5 20h13a1.5 1.5 0 001.5-1.5V15"/>',
    "camera": '<path d="M4 8.5A1.5 1.5 0 015.5 7h2l1.5-2.5h6L16.5 7h2A1.5 1.5 0 0120 8.5v9A1.5 1.5 0 0118.5 19h-13A1.5 1.5 0 014 17.5z"/><circle cx="12" cy="12.8" r="3.3"/>',
    "refresh": '<path d="M19.5 12a7.5 7.5 0 11-2.2-5.3"/><path d="M19.5 4.5v4h-4"/>',
    "external": '<path d="M13.5 4.5h6v6M19.5 4.5l-8.5 8.5"/><path d="M17 14v4.5a1.5 1.5 0 01-1.5 1.5h-10A1.5 1.5 0 014 18.5v-10A1.5 1.5 0 015.5 7H10"/>',
    "copy": '<rect x="8.5" y="8.5" width="11.5" height="11.5" rx="2"/><path d="M15.5 8.5V5.5A1.5 1.5 0 0014 4H5.5A1.5 1.5 0 004 5.5V14a1.5 1.5 0 001.5 1.5h3"/>',
    "wand": '<path d="M4.5 19.5l10-10M13 6.5l1.5-1.5 3 3L16 9.5"/><path d="M18.5 3v2.5M17.25 4.25h2.5M20 10v2M19 11h2M8 3.5v2M7 4.5h2"/>',
    "more": '<circle cx="5.5" cy="12" r="1.3"/><circle cx="12" cy="12" r="1.3"/><circle cx="18.5" cy="12" r="1.3"/>',
    "info": '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5.5M12 7.8v.1"/>',
    "warning": '<path d="M12 4l9 15.5H3z"/><path d="M12 10v4.5M12 17v.1"/>',
    "error": '<circle cx="12" cy="12" r="8.5"/><path d="M9 9l6 6M15 9l-6 6"/>',
    "play": '<path d="M8 5.5v13l10.5-6.5z"/>',
    "stop": '<rect x="6.5" y="6.5" width="11" height="11" rx="1.5"/>',
    "folder": '<path d="M3.5 7A1.5 1.5 0 015 5.5h4.5l2 2.5H19A1.5 1.5 0 0120.5 9.5v8A1.5 1.5 0 0119 19H5a1.5 1.5 0 01-1.5-1.5z"/>',
    "user": '<circle cx="12" cy="8.5" r="3.8"/><path d="M4.5 20c1.2-3.6 4-5.3 7.5-5.3s6.3 1.7 7.5 5.3"/>',
    "logout": '<path d="M14 4.5H6A1.5 1.5 0 004.5 6v12A1.5 1.5 0 006 19.5h8"/><path d="M10 12h10M16.5 8.5L20 12l-3.5 3.5"/>',
    "key": '<circle cx="8" cy="15" r="4"/><path d="M11 12l8.5-8.5M16.5 6.5l2.5 2.5M14 9l2 2"/>',
    "pin": '<path d="M12 21s-6.5-5.6-6.5-11A6.5 6.5 0 0112 3.5 6.5 6.5 0 0118.5 10c0 5.4-6.5 11-6.5 11z"/><circle cx="12" cy="10" r="2.3"/>',
    "arrow-right": '<path d="M4.5 12h15M14 6.5l5.5 5.5-5.5 5.5"/>',
    "lock": '<rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8 10.5V8a4 4 0 018 0v2.5"/>',
    "plug": '<path d="M9 3.5v5M15 3.5v5M6.5 8.5h11v3a5.5 5.5 0 01-11 0zM12 17v3.5"/>',
    "layers": '<path d="M12 3.5l8.5 4.5-8.5 4.5L3.5 8z"/><path d="M3.5 12l8.5 4.5 8.5-4.5M3.5 16l8.5 4.5 8.5-4.5"/>',
    "drag": '<circle cx="9" cy="6" r="1.2"/><circle cx="15" cy="6" r="1.2"/><circle cx="9" cy="12" r="1.2"/><circle cx="15" cy="12" r="1.2"/><circle cx="9" cy="18" r="1.2"/><circle cx="15" cy="18" r="1.2"/>',
}

FILLED = {
    "star-fill": '<path d="M12 3.2l2.75 5.55 6.1.9-4.42 4.3 1.05 6.08L12 17.15l-5.48 2.88 1.05-6.08-4.42-4.3 6.1-.9z"/>',
    "dot": '<circle cx="12" cy="12" r="5"/>',
    "knob": '<circle cx="12" cy="12" r="11"/>',
}


def render(svg, path, size):
    data = resvg_py.svg_to_bytes(svg_string=svg, width=size, height=size)
    with open(path, "wb") as f:
        f.write(bytes(data))


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, body in ICONS.items():
        render(HEAD + body + "</svg>", os.path.join(OUT, name + ".png"), SIZE)
    for name, body in FILLED.items():
        render(FILL + body + "</svg>", os.path.join(OUT, name + ".png"), SIZE)
    # Scenario logo: the stacked mark (white, tinted per theme) and the wordmarks.
    mark = open(os.path.join(LOGO_SRC, "scenario-white.svg")).read()
    render(mark, os.path.join(OUT, "scenario-mark.png"), 128)
    for variant in ("black", "white"):
        svg = open(os.path.join(LOGO_SRC, "scenario-full-%s.svg" % variant)).read()
        data = resvg_py.svg_to_bytes(svg_string=svg, width=512, height=126)
        with open(os.path.join(OUT, "scenario-wordmark-%s.png" % variant), "wb") as f:
            f.write(bytes(data))
    print("icons:", len(ICONS) + len(FILLED), "->", OUT)


if __name__ == "__main__":
    main()
