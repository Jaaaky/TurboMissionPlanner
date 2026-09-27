# /// script
# requires-python = ">=3.10"
# dependencies = ["fonttools>=4.50"]
# ///
"""Build the Turbo Sans script fonts from pinned IBM Plex sources.

Usage (from the repo root):  uv run Fonts/tools/build_fonts.py

Outputs Fonts/TurboSans{Arabic,SC,TC,JP,KR}-{Regular,Bold}.ttf. The IBM Plex
license reserves the name "Plex", so every modified font gets a new family name.

- Turbo Sans Arabic: IBM Plex Sans Arabic plus zero-width glyphs for
  U+200C..U+200F. Persian text uses ZWNJ constantly and Plex Arabic has no
  glyph for it.
- Turbo Sans SC/TC/JP/KR: IBM Plex Sans SC/TC/JP/KR subset to Latin, common
  punctuation, the characters our translations use and the common national
  set of that language, so each file stays a few MB.

Rerun after translation updates: the subsets read the .resx files.
"""
import glob
import hashlib
import os
import sys
import urllib.request
import xml.etree.ElementTree as ET

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph

PLEX_COMMIT = "763c36ef9117782905ae010056dfbe8fd2653a25"
PLEX_RAW = "https://raw.githubusercontent.com/IBM/plex/" + PLEX_COMMIT + "/packages/"
SOURCES = {
    "IBMPlexSansArabic-Regular.ttf": ("plex-sans-arabic/fonts/complete/ttf/", "8e0f1046c736bf939d4939ee3ae0116acf61cbcd6592deae7656761627080981"),
    "IBMPlexSansArabic-Bold.ttf": ("plex-sans-arabic/fonts/complete/ttf/", "b74f809dead12442ed56e02a12c3bcc02076c9ad4e32f17d0a9ca6fc1aafc89e"),
    "IBMPlexSansSC-Regular.ttf": ("plex-sans-sc/fonts/complete/ttf/hinted/", "012e587c5a78d25f456057614b880fc7450b748c352573d8052245df2a71158a"),
    "IBMPlexSansSC-Bold.ttf": ("plex-sans-sc/fonts/complete/ttf/hinted/", "3e94ef1df394eae336cc1bc98f2160b58e2cab186f47d820e4c808aef1f4e28f"),
    "IBMPlexSansTC-Regular.ttf": ("plex-sans-tc/fonts/complete/ttf/hinted/", "654677156ffc9ba35503ff569a4d55bc7044501f055bd7752a404445ed31cee7"),
    "IBMPlexSansTC-Bold.ttf": ("plex-sans-tc/fonts/complete/ttf/hinted/", "3462a66b8be1f52a0408ebc16c099e0b9caae0bfa8172ecb9e7f34267c35f98e"),
    "IBMPlexSansJP-Regular.ttf": ("plex-sans-jp/fonts/complete/ttf/hinted/", "e5e9ee949e05ca25bf75be44d6412c7071fcdeb8ca6c4361a8d6aeb81f96289a"),
    "IBMPlexSansJP-Bold.ttf": ("plex-sans-jp/fonts/complete/ttf/hinted/", "1bc9fabb696915df66f59f99fe450b21fa1d6e48749213d182b173367832a118"),
    "IBMPlexSansKR-Regular.ttf": ("plex-sans-kr/fonts/complete/ttf/hinted/", "193af4c0c4f979251edd13708ea4c3eb4d0d07f9352b3d2a58b43b042767183b"),
    "IBMPlexSansKR-Bold.ttf": ("plex-sans-kr/fonts/complete/ttf/hinted/", "2aa49909a6dae0591efa1cc892bf99a2556b3b56fbcb639439240010518548ae"),
}

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(REPO, "Fonts")
CACHE = os.path.join(os.environ.get("XDG_CACHE_HOME", os.path.expanduser("~/.cache")), "turbo-font-sources", PLEX_COMMIT)

ZERO_WIDTH = [0x200C, 0x200D, 0x200E, 0x200F]  # ZWNJ, ZWJ, LRM, RLM

# Latin, Latin-1, general punctuation, arrows, CJK symbols, fullwidth forms.
BASE = (set(range(0x20, 0x7F)) | set(range(0xA0, 0x180)) | set(range(0x2000, 0x2070))
        | set(range(0x2190, 0x2200)) | set(range(0x3000, 0x3040)) | set(range(0xFF00, 0xFFF0))
        | {0x02C5, 0x02C6, 0x02C7, 0x02DA, 0x02DC, 0x2103, 0x2116, 0x2122})


def decode_range(codec, hi_range, lo_range):
    out = set()
    for hi in hi_range:
        for lo in lo_range:
            try:
                ch = bytes([hi, lo]).decode(codec)
            except UnicodeDecodeError:
                continue
            if len(ch) == 1:
                out.add(ord(ch))
    return out


GB2312_HANZI = decode_range("gb2312", range(0xB0, 0xF8), range(0xA1, 0xFF))
BIG5_L1 = {c for c in decode_range("big5", range(0xA4, 0xC7), list(range(0x40, 0x7F)) + list(range(0xA1, 0xFF)))
           if 0x4E00 <= c <= 0x9FFF}
JIS_L1_L2 = decode_range("euc_jp", range(0xB0, 0xF5), range(0xA1, 0xFF))
KANA = set(range(0x3040, 0x3100)) | set(range(0x31F0, 0x3200))
KSX1001_HANGUL = decode_range("euc_kr", range(0xB0, 0xC9), range(0xA1, 0xFF)) | set(range(0x3130, 0x3190))

# script -> (source stem, translation cultures, common set)
CJK = {
    "SC": ("IBMPlexSansSC", ["zh-Hans", "zh-CN"], GB2312_HANZI),
    "TC": ("IBMPlexSansTC", ["zh-Hant", "zh-TW", "zh-HK"], BIG5_L1),
    "JP": ("IBMPlexSansJP", ["ja-JP", "ja"], JIS_L1_L2 | KANA),
    "KR": ("IBMPlexSansKR", ["ko-KR", "ko"], KSX1001_HANGUL),
}


def source(name):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name)
    sub, sha = SOURCES[name]
    if not os.path.exists(path):
        print("download", name)
        urllib.request.urlretrieve(PLEX_RAW + sub + name, path + ".part")
        os.replace(path + ".part", path)
    digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
    if digest != sha:
        sys.exit("sha256 mismatch for %s: %s" % (name, digest))
    return path


def translation_chars(cultures):
    chars = set()
    for culture in cultures:
        for f in glob.glob(os.path.join(REPO, "**", "*.%s.resx" % culture), recursive=True):
            if os.sep + "ExtLibs" + os.sep + "mono" + os.sep in f:
                continue
            for data in ET.parse(f).getroot().findall("data"):
                if data.get("type") or data.get("mimetype"):
                    continue
                value = data.find("value")
                if value is not None and value.text:
                    chars.update(ord(c) for c in value.text)
    return chars


def rename(font, family, style):
    """Replace every naming record that carries the old family name."""
    name = font["name"]
    version = name.getDebugName(5) or "Version 1.0"
    ps_family = family.replace(" ", "")
    full = family if style == "Regular" else family + " " + style
    name.names = [n for n in name.names if n.nameID not in (1, 2, 3, 4, 6, 16, 17, 18, 21, 22)]
    for nid, text in ((1, family), (2, style), (3, "%s %s; %s" % (family, style, version)), (4, full),
                      (6, "%s-%s" % (ps_family, style))):
        name.setName(text, nid, 3, 1, 0x409)
        name.setName(text, nid, 1, 0, 0)
    note = ("Modified for Mission Planner (Turbo) from IBM Plex: renamed (Reserved Font Name \"Plex\"), "
            "subset and/or extended. Licensed under the SIL Open Font License 1.1.")
    name.setName(note, 10, 3, 1, 0x409)
    if "CFF " in font:
        sys.exit("unexpected CFF outlines in " + family)


def add_zero_width(font):
    """Add empty, zero-advance glyphs for the characters missing from the cmap."""
    cmap = font.getBestCmap()
    missing = [cp for cp in ZERO_WIDTH if cp not in cmap]
    if not missing:
        return []
    order = font.getGlyphOrder()
    glyf = font["glyf"]
    for cp in missing:
        gname = "uni%04X" % cp
        order.append(gname)
        glyf.glyphs[gname] = Glyph()  # empty outline
        font["hmtx"].metrics[gname] = (0, 0)
        for table in font["cmap"].tables:
            if table.isUnicode():
                table.cmap[cp] = gname
    font.setGlyphOrder(order)
    glyf.glyphOrder = order
    if "post" in font and hasattr(font["post"], "extraNames"):
        font["post"].extraNames = []  # recomputed on save
    return missing


def save(font, filename):
    path = os.path.join(OUT, filename)
    font.save(path)
    print("wrote %-28s %8.2f MB" % (filename, os.path.getsize(path) / 1e6))


def build_arabic():
    for style in ("Regular", "Bold"):
        font = TTFont(source("IBMPlexSansArabic-%s.ttf" % style))
        added = add_zero_width(font)
        rename(font, "Turbo Sans Arabic", style)
        print("Turbo Sans Arabic %s: added zero-width %s" % (style, ["U+%04X" % c for c in added]))
        save(font, "TurboSansArabic-%s.ttf" % style)


def build_cjk():
    for tag, (stem, cultures, common) in CJK.items():
        wanted = BASE | common | translation_chars(cultures)
        for style in ("Regular", "Bold"):
            font = TTFont(source("%s-%s.ttf" % (stem, style)))
            options = subset.Options()
            options.layout_features = ["*"]
            options.name_IDs = ["*"]
            options.name_languages = ["*"]
            options.notdef_outline = True
            options.hinting = True
            options.glyph_names = False
            options.drop_tables += ["meta"]
            subsetter = subset.Subsetter(options)
            subsetter.populate(unicodes=wanted)
            subsetter.subset(font)
            rename(font, "Turbo Sans " + tag, style)
            have = set(font.getBestCmap())
            lost = sorted(c for c in translation_chars(cultures) if c not in have and c > 0x7E
                          and not chr(c).isspace())
            if lost:
                print("WARNING Turbo Sans %s %s lacks %d translation chars: %s"
                      % (tag, style, len(lost), "".join(chr(c) for c in lost)))
            save(font, "TurboSans%s-%s.ttf" % (tag, style))


if __name__ == "__main__":
    build_arabic()
    build_cjk()
