"""Probes /fiscalisation for the two things a screenshot cannot answer.

A headless shot showed the Bootstrap icon glyphs missing and the row checkboxes
nearly invisible. Either could be a real defect or an artifact of the capture, and
the pixels do not say which — so this asks the page directly:

  * whether the Bootstrap Icons webfont actually loaded (it comes from jsDelivr,
    which a headless run may simply not reach), and
  * what `color-scheme` the console resolves to, which is what decides how the
    browser paints a native checkbox on a dark ground.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from cdp import Chrome  # noqa: E402
from verify import WEB, _login  # noqa: E402


def main():
    with Chrome(headless=True, width=1600, height=1000) as c:
        c.install_error_trap()
        _login(c)
        c.goto(WEB + "/fiscalisation")
        c.wait_for(".fcx-table-queue tbody .fcx-check", timeout=30)

        out = {}

        # Is the icon font there at all? document.fonts is the authority; the
        # glyph being blank tells you nothing about why.
        out["bootstrap_icons_loaded"] = c.eval(
            "document.fonts.check('16px \"bootstrap-icons\"')")
        out["icon_font_faces"] = c.eval(
            "[...document.fonts].filter(f => /bootstrap/i.test(f.family))"
            ".map(f => f.family + ':' + f.status)")
        out["icon_computed_family"] = c.eval(
            "(() => { const i = document.querySelector('.fcx-btn .bi');"
            " return i ? getComputedStyle(i, '::before').fontFamily : null; })()")
        out["icon_glyph_has_width"] = c.eval(
            "(() => { const i = document.querySelector('.fcx-btn .bi');"
            " return i ? i.getBoundingClientRect().width > 0 : null; })()")

        for dark in (True, False):
            c.set_theme(dark)
            key = "dark" if dark else "light"
            out[key] = {
                "root_color_scheme": c.eval(
                    "getComputedStyle(document.querySelector('.fcx')).colorScheme"),
                "html_color_scheme": c.eval(
                    "getComputedStyle(document.documentElement).colorScheme"),
                "checkbox_accent": c.eval(
                    "getComputedStyle(document.querySelector("
                    "'.fcx-table-queue tbody .fcx-check')).accentColor"),
                "checkbox_scheme": c.eval(
                    "getComputedStyle(document.querySelector("
                    "'.fcx-table-queue tbody .fcx-check')).colorScheme"),
                "band_count_colour": c.eval(
                    "getComputedStyle(document.querySelector('.fcx-band-count')).color"),
                "surface": c.eval(
                    "getComputedStyle(document.querySelector('.fcx-band-lead'))"
                    ".backgroundColor"),
            }

        out["console_errors"] = c.console_errors()

    print(json.dumps(out, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
