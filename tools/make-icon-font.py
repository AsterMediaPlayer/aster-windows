"""Makes Aster's icon fonts for systems without Segoe Fluent Icons (Linux).

The app's XAML names its icons by Segoe Fluent Icons code points (&#xE768;
is Play). Segoe can't be shipped with the app, so this takes the matching
icons from Microsoft's Fluent UI System Icons (MIT, fluentui-system-icons on
GitHub) and puts each at the Segoe code point the app already uses, in two
small fonts: "Aster Icons" (outlines) and "Aster Icons Filled" (the few
filled states). The app lists them after Segoe, so Windows keeps Segoe and
anywhere else gets these, with no change to the XAML.

    pip install fonttools
    python make-icon-font.py <folder with FluentSystemIcons-Regular/Filled .ttf and .json>

A new icon in the app needs a line in ICONS, and this run again.
"""

import json
import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._c_m_a_p import CmapSubtable

# Segoe Fluent Icons code point: Fluent UI System Icons name (24px).
ICONS = {
    0xE70D: "chevron_down",
    0xE70E: "chevron_up",
    0xE711: "dismiss",
    0xE713: "settings",
    0xE714: "video",
    0xE721: "search",
    0xE72B: "arrow_left",
    0xE72C: "arrow_clockwise",
    0xE72E: "lock_closed",
    0xE734: "star",
    0xE740: "full_screen_maximize",
    0xE767: "speaker_2",
    0xE768: "play",
    0xE769: "pause",
    0xE76B: "chevron_left",
    0xE76C: "chevron_right",
    0xE77B: "person",
    0xE787: "calendar_ltr",
    0xE7C8: "record",
    0xE7F0: "closed_caption",
    0xE7F4: "tv",
    0xE80F: "home",
    0xE823: "clock",
    0xE82D: "book",
    0xE892: "previous",
    0xE893: "next",
    0xE8B1: "arrow_shuffle",
    0xE8B2: "movies_and_tv",
    0xE8B7: "folder",
    0xE8BD: "text_quote",
    0xE8D6: "music_note_2",
    0xE8D7: "person_key",
    0xE8ED: "arrow_repeat_1",
    0xE8EE: "arrow_repeat_all",
    0xE8F1: "library",
    0xE8FD: "text_bullet_list_ltr",
    0xE930: "checkmark_circle",
    0xE9E9: "options",
    0xEA86: "puzzle_piece",
    0xEA3A: "circle",
    0xEB51: "heart",
    0xEC05: "live",
    0xF3B1: "sign_out",
}

# The filled states: a favourite's heart, a favourite star, watched.
FILLED = {
    0xE735: "star",
    0xEB52: "heart",
    0xEC61: "checkmark_circle",
}


def build(source, names, style, family, destination):
    font = TTFont(os.path.join(source, f"FluentSystemIcons-{style}.ttf"))
    codes = json.load(open(os.path.join(source, f"FluentSystemIcons-{style}.json"), encoding="utf-8"))
    by_code = font.getBestCmap()
    glyphs = {}
    for point, name in names.items():
        key = f"ic_fluent_{name}_24_{style.lower()}"
        glyphs[point] = by_code[codes[key]]

    options = subset.Options()
    options.glyph_names = True
    options.notdef_outline = True
    options.name_IDs = ["*"]
    subsetter = subset.Subsetter(options)
    subsetter.populate(glyphs=sorted(set(glyphs.values())))
    subsetter.subset(font)

    # The icons at Segoe's code points instead of Fluent's.
    tables = []
    for platform, encoding in ((3, 1), (0, 3)):
        table = CmapSubtable.newSubtable(4)
        table.platformID, table.platEncID, table.language = platform, encoding, 0
        table.cmap = dict(glyphs)
        tables.append(table)
    font["cmap"].tables = tables

    for record in font["name"].names:
        if record.nameID in (1, 16):
            record.string = family
        elif record.nameID in (4,):
            record.string = family if style == "Regular" else f"{family}"
        elif record.nameID == 6:
            record.string = family.replace(" ", "")
        elif record.nameID in (2, 17):
            record.string = "Regular"
    font.save(destination)
    print(f"{destination}: {len(glyphs)} icons")


def main():
    source = sys.argv[1]
    out = os.path.join(os.path.dirname(__file__), "..", "src", "Aster.Desktop", "Assets", "Fonts")
    os.makedirs(out, exist_ok=True)
    build(source, ICONS, "Regular", "Aster Icons", os.path.join(out, "AsterIcons.ttf"))
    build(source, FILLED, "Filled", "Aster Icons Filled", os.path.join(out, "AsterIconsFilled.ttf"))


if __name__ == "__main__":
    main()
