Fonts shipped with Mission Planner (Turbo)

IBMPlexSans-*.ttf (embedded in MissionPlanner.exe)
  IBM Plex Sans, unmodified. The default UI font.

TurboSans*.ttf (these files)
  UI fonts for languages IBM Plex Sans does not cover:
    Turbo Sans Arabic  Arabic, Persian, Uyghur
    Turbo Sans SC      Simplified Chinese
    Turbo Sans TC      Traditional Chinese
    Turbo Sans JP      Japanese
    Turbo Sans KR      Korean
  Modified versions of IBM Plex Sans Arabic / SC / TC / JP / KR
  (github.com/IBM/plex, commit 763c36ef9117782905ae010056dfbe8fd2653a25):
  renamed as the license requires (Reserved Font Name "Plex"), the CJK
  fonts subset to Latin, punctuation and the common characters of their
  language, and zero-width U+200C..U+200F glyphs added to the Arabic font.
  Rebuild with: uv run Fonts/tools/build_fonts.py

All fonts: Copyright (c) 2017 IBM Corp., SIL Open Font License 1.1 (LICENSE.txt).
IBM Plex is a trademark of IBM Corp.
