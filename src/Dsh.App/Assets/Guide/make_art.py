#!/usr/bin/env python3
"""Draws the DGX Spark setup guide's pictures.

Every picture is a flat illustration in the app's palette (the logo's #4338ca -> #2563eb -> #06b6d4
gradient), written as SVG in a light and a dark variant (NAME.svg, NAME-dark.svg) and rendered to
PNG at 2x (NAME.png, NAME-dark.png) for the app. The Spark Swapper pictures frame the project's own
screenshots (MIT licensed, see SWAPPER-SCREENSHOTS-LICENSE.txt).

    PYTHONPATH=<cairosvg + Pillow> python3 make_art.py [--swapper <DGX-Spark-Swapper checkout>]

Text uses Inter and JetBrains Mono when installed (falling back to any sans / mono font).
"""
import base64
import io
import os
import sys

import cairosvg
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
W, H = 600, 270
SCALE = 2
SANS = "Inter, 'Segoe UI', 'Liberation Sans', Arial, sans-serif"
MONO = "'JetBrains Mono', 'Cascadia Mono', Consolas, 'DejaVu Sans Mono', monospace"

LIGHT = dict(
    name="light", bg1="#eef2ff", bg2="#e3f6fb", blob="#c7d2fe", blob2="#a5f3fc",
    ink="#1e293b", muted="#64748b", faint="#94a3b8", surface="#ffffff", surface2="#f1f5f9", surface3="#e2e8f0",
    stroke="#cbd5e1", shadow="#1e293b", shadow_op=0.10, screen="#0f172a", screen_ink="#e2e8f0",
    accent="#2563eb", accent_soft="#dbeafe", ok="#16a34a", ok_soft="#dcfce7", warn="#d97706", warn_soft="#fef3c7",
    bad="#dc2626", bad_soft="#fee2e2", desk="#dbe3ee", cable="#475569",
)
DARK = dict(
    name="dark", bg1="#1b2040", bg2="#0f2b38", blob="#312e81", blob2="#155e75",
    ink="#e2e8f0", muted="#94a3b8", faint="#64748b", surface="#1e293b", surface2="#273449", surface3="#334155",
    stroke="#3e4f69", shadow="#000000", shadow_op=0.35, screen="#0b1220", screen_ink="#e2e8f0",
    accent="#60a5fa", accent_soft="#1e3a8a", ok="#22c55e", ok_soft="#14532d", warn="#f59e0b", warn_soft="#78350f",
    bad="#f87171", bad_soft="#7f1d1d", desk="#2a3950", cable="#94a3b8",
)


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


# MARK: - primitives

def text(x, y, s, size=12, weight=400, fill="#000", anchor="start", family=SANS, opacity=1, spacing=0):
    extra = f' letter-spacing="{spacing}"' if spacing else ""
    op = f' opacity="{opacity}"' if opacity != 1 else ""
    return (f'<text x="{x}" y="{y}" font-family="{family}" font-size="{size}" font-weight="{weight}" '
            f'fill="{fill}" text-anchor="{anchor}"{extra}{op}>{esc(s)}</text>')


def rect(x, y, w, h, fill, rx=0, stroke=None, sw=1, opacity=1, extra=""):
    st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
    op = f' opacity="{opacity}"' if opacity != 1 else ""
    return f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{fill}"{st}{op} {extra}/>'


def circle(cx, cy, r, fill, stroke=None, sw=1, opacity=1):
    st = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
    op = f' opacity="{opacity}"' if opacity != 1 else ""
    return f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{fill}"{st}{op}/>'


def path(d, fill="none", stroke=None, sw=1.5, opacity=1, cap="round", join="round", dash=None):
    st = f' stroke="{stroke}" stroke-width="{sw}" stroke-linecap="{cap}" stroke-linejoin="{join}"' if stroke else ""
    op = f' opacity="{opacity}"' if opacity != 1 else ""
    da = f' stroke-dasharray="{dash}"' if dash else ""
    return f'<path d="{d}" fill="{fill}"{st}{op}{da}/>'


def shadow(t, x, y, w, h, rx=10, dy=4):
    return rect(x + 1, y + dy, w, h, t["shadow"], rx=rx, opacity=t["shadow_op"])


def card(t, x, y, w, h, rx=10, fill=None, stroke=True):
    return shadow(t, x, y, w, h, rx) + rect(x, y, w, h, fill or t["surface"], rx=rx, stroke=t["stroke"] if stroke else None)


def badge(x, y, n, r=11):
    return circle(x, y, r, "url(#brand)") + text(x, y + 4.2, str(n), 12, 700, "#ffffff", "middle")


def check(x, y, r, fill):
    return circle(x, y, r, fill) + path(f"M{x - r * .45},{y + r * .02} L{x - r * .1},{y + r * .38} L{x + r * .48},{y - r * .35}",
                                         stroke="#ffffff", sw=max(1.6, r * .22))


def cross(x, y, r, fill):
    k = r * .38
    return circle(x, y, r, fill) + path(f"M{x - k},{y - k} L{x + k},{y + k} M{x + k},{y - k} L{x - k},{y + k}", stroke="#ffffff", sw=max(1.6, r * .22))


def pill(x, y, s, fill, color, size=10.5, pad=8, h=20, weight=600, anchor="start", family=SANS):
    w = len(s) * size * 0.58 + pad * 2
    if anchor == "middle":
        x -= w / 2
    return rect(x, y, w, h, fill, rx=h / 2) + text(x + w / 2, y + h / 2 + size * 0.36, s, size, weight, color, "middle", family)


def wifi(x, y, s, color, opacity=1):
    out = circle(x, y, 2.2 * s, color, opacity=opacity)
    for i, r in enumerate((7, 12.5, 18)):
        rr = r * s
        out += path(f"M{x - rr * .72},{y - rr * .7} A{rr},{rr} 0 0 1 {x + rr * .72},{y - rr * .7}", stroke=color, sw=2.4 * s,
                    opacity=opacity * (1 - i * .18))
    return out


def lock(x, y, s, color, open_=False):
    body = rect(x - 6 * s, y - 1 * s, 12 * s, 10 * s, color, rx=2 * s)
    shackle = (f"M{x - 3.8 * s},{y - 1 * s} V{y - 4.5 * s} A{3.8 * s},{3.8 * s} 0 0 1 {x + 3.8 * s},{y - 4.5 * s}"
               + (f" V{y - 3 * s}" if open_ else f" V{y - 1 * s}"))
    return path(shackle, stroke=color, sw=1.8 * s) + body


def warn_triangle(x, y, s, fill):
    return (path(f"M{x},{y - 9 * s} L{x + 10 * s},{y + 8 * s} L{x - 10 * s},{y + 8 * s} Z", fill=fill, stroke=fill, sw=2 * s)
            + rect(x - 1.1 * s, y - 3.5 * s, 2.2 * s, 6.5 * s, "#ffffff", rx=1 * s)
            + circle(x, y + 5.2 * s, 1.3 * s, "#ffffff"))


def cursor(x, y, s=1, t=None):
    return path(f"M{x},{y} L{x},{y + 16 * s} L{x + 4.2 * s},{y + 12.2 * s} L{x + 7 * s},{y + 18.5 * s} L{x + 9.6 * s},{y + 17.3 * s} "
                f"L{x + 6.8 * s},{y + 11.2 * s} L{x + 12 * s},{y + 11.2 * s} Z", fill="#ffffff", stroke="#0f172a", sw=1.2 * s)


# MARK: - objects

def spark(t, x, y, w, led=True):
    """The DGX Spark: a small, flat box with a champagne metal-foam front."""
    h = w * 0.36
    dx, dy = w * 0.12, w * 0.10
    out = ""
    out += f'<ellipse cx="{x + w / 2 + dx * .6}" cy="{y + h + 5}" rx="{w * .62}" ry="{h * .16}" fill="{t["shadow"]}" opacity="{t["shadow_op"] * 1.6}"/>'
    out += path(f"M{x},{y} L{x + dx},{y - dy} L{x + w + dx},{y - dy} L{x + w},{y} Z", fill="#e6cf9f", stroke="#a88549", sw=1)
    out += path(f"M{x + w},{y} L{x + w + dx},{y - dy} L{x + w + dx},{y + h - dy} L{x + w},{y + h} Z", fill="#a17c3f", stroke="#8a6a33", sw=1)
    out += rect(x, y, w, h, "#c9a462", stroke="#8a6a33", sw=1)
    out += rect(x + 3, y + 3, w - 6, h - 6, "url(#mesh)")
    if led:
        out += circle(x + w - 9, y + h - 7, 2.3, "#4ade80") + circle(x + w - 9, y + h - 7, 4.5, "#4ade80", opacity=.25)
    return out


def laptop(t, x, y, w, inner="", screen=None):
    """Returns (svg, (sx, sy, sw, sh)) — the screen's rectangle for content."""
    sh = w * 0.6
    out = shadow(t, x, y, w, sh + 8, rx=8)
    out += rect(x, y, w, sh, "#334155", rx=8)
    sx, sy, sw_, shh = x + 7, y + 7, w - 14, sh - 12
    out += rect(sx, sy, sw_, shh, screen or t["surface"], rx=3)
    out += inner
    out += path(f"M{x - w * .07},{y + sh + 9} L{x + w * 1.07},{y + sh + 9} L{x + w},{y + sh} L{x},{y + sh} Z", fill="#94a3b8")
    out += rect(x + w * .42, y + sh + 1.5, w * .16, 3, "#64748b", rx=1.5)
    return out, (sx, sy, sw_, shh)


def monitor(t, x, y, w, h, inner="", screen=None):
    out = shadow(t, x, y, w, h, rx=8)
    out += rect(x, y, w, h, "#1f2937", rx=8)
    out += rect(x + 7, y + 7, w - 14, h - 14, screen or t["screen"], rx=3)
    out += inner
    out += rect(x + w / 2 - 9, y + h, 18, 16, "#475569")
    out += rect(x + w / 2 - 36, y + h + 14, 72, 6, "#64748b", rx=3)
    return out


def browser(t, x, y, w, h, url, inner="", secure="lock", tab="Spark"):
    out = card(t, x, y, w, h, rx=10)
    out += f'<path d="M{x},{y + 10} A10,10 0 0 1 {x + 10},{y} H{x + w - 10} A10,10 0 0 1 {x + w},{y + 10} V{y + 30} H{x} Z" fill="{t["surface2"]}"/>'
    out += circle(x + 14, y + 15, 3.6, "#f87171") + circle(x + 26, y + 15, 3.6, "#fbbf24") + circle(x + 38, y + 15, 3.6, "#34d399")
    ax, aw = x + 52, w - 64
    out += rect(ax, y + 6, aw, 18, t["surface"], rx=9, stroke=t["stroke"])
    if secure == "lock":
        out += lock(ax + 11, y + 15.5, .62, t["muted"])
        tx = ax + 20
    elif secure == "warn":
        out += warn_triangle(ax + 12, y + 15, .5, t["bad"])
        out += text(ax + 21, y + 18.8, "Not secure", 9.5, 700, t["bad"])
        tx = ax + 76
    else:
        tx = ax + 10
    out += text(tx, y + 18.8, url, 10, 500, t["ink"], family=SANS)
    out += path(f"M{x},{y + 30} H{x + w}", stroke=t["stroke"], sw=1)
    return out + inner


def router(t, x, y):
    out = f'<ellipse cx="{x + 38}" cy="{y + 25}" rx="44" ry="5" fill="{t["shadow"]}" opacity="{t["shadow_op"] * 1.5}"/>'
    out += path(f"M{x + 12},{y + 2} L{x + 6},{y - 22}", stroke="#475569", sw=3.2)
    out += path(f"M{x + 64},{y + 2} L{x + 70},{y - 22}", stroke="#475569", sw=3.2)
    out += rect(x, y, 76, 22, "#334155", rx=6) + rect(x, y, 76, 8, "#475569", rx=4)
    for i in range(4):
        out += circle(x + 14 + i * 9, y + 15, 2, "#4ade80" if i < 3 else "#60a5fa")
    return out


def usb_stick(x, y, angle=0, s=1, label="BOOTME", left=False):
    """A USB stick; its connector points right (or left, to plug into something on its left)."""
    body, plug = (18 * s, 0) if left else (0, 58 * s)
    g = (rect(body, 0, 58 * s, 24 * s, "url(#brand)", rx=6 * s)
         + rect(plug, 4 * s, 18 * s, 16 * s, "#cbd5e1", rx=1.5 * s, stroke="#94a3b8", sw=1)
         + rect(plug + 8 * s, 8 * s, 4 * s, 3 * s, "#64748b") + rect(plug + 8 * s, 13 * s, 4 * s, 3 * s, "#64748b")
         + circle(body + (53 if left else 6) * s, 12 * s, 2.5 * s, "#ffffff", opacity=.35)
         + text(body + 29 * s, 15.6 * s, label, 8 * s, 700, "#ffffff", "middle", spacing=.5))
    return f'<g transform="translate({x},{y}) rotate({angle})">{g}</g>'


def keyboard(t, x, y, w, highlight=(), labels=True):
    out = shadow(t, x, y, w, 40, rx=6) + rect(x, y, w, 40, t["surface3"], rx=6, stroke=t["stroke"])
    keys = [["Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "Del"], ["", "", "", "", "", "", "", "", "", ""]]
    kw = (w - 12 - 9 * 3) / 10
    for r, row in enumerate(keys):
        for i, k in enumerate(row):
            kx, ky = x + 6 + i * (kw + 3), y + 5 + r * 16
            hot = k in highlight
            out += rect(kx, ky, kw, 13, "url(#brand)" if hot else t["surface"], rx=2.5, stroke=None if hot else t["stroke"])
            if k and labels:
                out += text(kx + kw / 2, ky + 9.4, k, 6.5 if not hot else 7, 700 if hot else 500, "#ffffff" if hot else t["muted"], "middle")
    return out


def cable(d, color, sw=3.2):
    return path(d, stroke=color, sw=sw)


def defs(t):
    return f'''<defs>
<linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{t["bg1"]}"/><stop offset="1" stop-color="{t["bg2"]}"/></linearGradient>
<linearGradient id="brand" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#4338ca"/><stop offset="0.55" stop-color="#2563eb"/><stop offset="1" stop-color="#06b6d4"/></linearGradient>
<linearGradient id="brandH" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#4338ca"/><stop offset="0.55" stop-color="#2563eb"/><stop offset="1" stop-color="#06b6d4"/></linearGradient>
<pattern id="mesh" width="5" height="5" patternUnits="userSpaceOnUse"><rect width="5" height="5" fill="#c09a57"/><circle cx="2.5" cy="2.5" r="1.25" fill="#e9d4a4" opacity="0.8"/></pattern>
<clipPath id="frame"><rect x="0" y="0" width="{W}" height="{H}" rx="18"/></clipPath>
</defs>'''


def frame(t, body, blobs=True):
    deco = ""
    if blobs:
        deco = (circle(W - 40, 30, 90, t["blob"], opacity=.35) + circle(40, H - 10, 70, t["blob2"], opacity=.30)
                + circle(W * .55, H + 30, 60, t["blob"], opacity=.18))
    return (f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{W}" height="{H}" viewBox="0 0 {W} {H}">'
            f'{defs(t)}<g clip-path="url(#frame)"><rect width="{W}" height="{H}" fill="url(#bg)"/>{deco}{body}</g></svg>')


def caption(t, x, y, title, sub=None, anchor="middle", size=12.5):
    out = text(x, y, title, size, 700, t["ink"], anchor)
    if sub:
        out += text(x, y + 15, sub, 10.5, 500, t["muted"], anchor)
    return out


# MARK: - pictures

def need(t):
    out = ""
    # Spark
    out += spark(t, 44, 128, 116)
    out += caption(t, 108, 196, "DGX Spark", "plus its power adapter")
    # Network
    out += router(t, 226, 120)
    out += wifi(264, 86, 1.05, t["accent"])
    out += caption(t, 264, 196, "Your home network", "Wi-Fi, or a network cable")
    # PC
    lap, (sx, sy, sw, sh) = laptop(t, 356, 90, 104)
    body = lap + rect(sx + 8, sy + 8, sw - 16, 8, t["accent_soft"], rx=3) + rect(sx + 8, sy + 22, sw * .55, 5, t["surface3"], rx=2.5) + rect(sx + 8, sy + 31, sw * .7, 5, t["surface3"], rx=2.5)
    out += body + circle(sx + sw - 13, sy + sh - 12, 7, "url(#brand)")
    out += caption(t, 408, 196, "This PC with DSH", "on the same network")
    # optional
    out += card(t, 488, 70, 100, 184, rx=12)
    out += text(538, 90, "ONLY TO", 8.5, 700, t["muted"], "middle", spacing=.8)
    out += text(538, 102, "REINSTALL", 8.5, 700, t["muted"], "middle", spacing=.8)
    out += usb_stick(508, 114, 0, .78, "16 GB+")
    out += keyboard(t, 503, 148, 70, labels=False)
    out += rect(515, 196, 46, 24, "#1f2937", rx=3) + rect(519, 200, 38, 16, t["screen"], rx=1.5) + rect(535, 220, 6, 4, "#475569")
    out += text(538, 236, "USB stick 16 GB+,", 8.5, 600, t["muted"], "middle")
    out += text(538, 247, "keyboard, screen", 8.5, 600, t["muted"], "middle")
    out += text(30, 38, "What you need", 17, 700, t["ink"])
    out += text(30, 56, "About 20 minutes, and nothing to type in a terminal.", 11, 500, t["muted"])
    return out


def choice(t):
    out = ""
    # left: new
    out += card(t, 36, 44, 250, 190, rx=16)
    out += spark(t, 96, 112, 118)
    for (x, y, s) in ((82, 84, 1), (236, 92, .7), (224, 156, .8)):
        out += path(f"M{x},{y - 7 * s} L{x + 2 * s},{y - 2 * s} L{x + 7 * s},{y} L{x + 2 * s},{y + 2 * s} L{x},{y + 7 * s} L{x - 2 * s},{y + 2 * s} L{x - 7 * s},{y} L{x - 2 * s},{y - 2 * s} Z", fill="#f59e0b")
    out += text(161, 190, "Brand new", 14, 700, t["ink"], "middle")
    out += text(161, 207, "Just out of the box —", 10.5, 500, t["muted"], "middle")
    out += text(161, 221, "never set up", 10.5, 500, t["muted"], "middle")
    # right: fresh
    out += card(t, 314, 44, 250, 190, rx=16)
    out += spark(t, 364, 120, 104)
    out += usb_stick(478, 124, 0, .72, "BOOTME", left=True)
    out += path("M427,76 A20,20 0 1 1 407,96", stroke=t["accent"], sw=3)
    out += path("M402,89 L407,97 L415,92", stroke=t["accent"], sw=3)
    out += text(439, 190, "Start fresh", 14, 700, t["ink"], "middle")
    out += text(439, 207, "Erase it and reinstall", 10.5, 500, t["muted"], "middle")
    out += text(439, 221, "from a USB stick", 10.5, 500, t["muted"], "middle")
    return out


def cables(t):
    out = rect(0, 222, W, 60, t["desk"], opacity=.6)
    # The Spark on the left; its ports are on the side facing us on the right.
    x, y, w = 70, 160, 180
    out += spark(t, x, y, w)
    side = x + w + w * 0.06
    # network: side port to the router
    out += router(t, 452, 140)
    out += cable(f"M{side},{y + 16} C{side + 60},{y + 16} {side + 80},{y - 22} 452,{y - 12}", "#3b82f6", 3.4)
    out += rect(side - 5, y + 12, 9, 8, "#1d4ed8", rx=1.5)
    # power: side port to the adapter, adapter to the wall
    out += cable(f"M{side},{y + 42} C{side + 50},{y + 42} {side + 40},{y + 76} 330,{y + 76}", t["cable"], 3.4)
    out += rect(side - 5, y + 38, 9, 8, "#1f2937", rx=1.5)
    out += rect(330, y + 64, 50, 26, "#334155", rx=5, stroke=t["stroke"]) + circle(355, y + 77, 3, "#4ade80")
    out += cable(f"M380,{y + 77} C420,{y + 77} 430,{y + 56} 470,{y + 56}", t["cable"], 3.4)
    out += rect(470, y + 44, 30, 24, t["surface"], rx=5, stroke=t["stroke"]) + circle(481, y + 56, 2, t["cable"]) + circle(489, y + 56, 2, t["cable"])
    out += badge(334, 92, 1) + text(350, 96.5, "Network cable — optional", 11.5, 700, t["ink"])
    out += text(350, 111, "Wi-Fi works too", 10, 500, t["muted"])
    out += badge(82, 250, 2) + text(98, 254.5, "Power — last.", 11.5, 700, t["ink"])
    out += text(184, 254.5, "It turns on by itself.", 10.5, 500, t["muted"])
    out += text(30, 38, "Cables first, power last", 17, 700, t["ink"])
    out += text(30, 56, "There's no power button: the Spark starts the moment it gets power.", 11, 500, t["muted"])
    return out


def quickstart_card(t):
    out = ""
    # the paper card, slightly rotated
    g = (rect(0, 0, 250, 170, "#fbfaf7", rx=6, stroke="#d6d3d1")
         + text(18, 30, "DGX Spark", 15, 700, "#1c1917") + text(18, 46, "Quick Start Guide", 10.5, 500, "#57534e")
         + rect(18, 60, 110, 5, "#e7e5e4", rx=2.5) + rect(18, 72, 90, 5, "#e7e5e4", rx=2.5) + rect(18, 84, 100, 5, "#e7e5e4", rx=2.5)
         + rect(18, 122, 60, 30, "#e7e5e4", rx=3))
    out += f'<g transform="translate(58,82) rotate(-4)">{shadow(t, 0, 0, 250, 170, 6)}{g}</g>'
    # sticker on the card
    sticker = (rect(0, 0, 150, 96, "#ffffff", rx=6, stroke="#a8a29e")
               + text(10, 17, "WI-FI HOTSPOT", 8, 700, "#78716c", spacing=.8)
               + text(10, 33, "Network  spark-3f2a", 10, 600, "#1c1917", family=MONO)
               + text(10, 48, "Password  7kq4-m2vx", 10, 600, "#1c1917", family=MONO)
               + text(10, 66, "SETUP PAGE", 8, 700, "#78716c", spacing=.8)
               + text(10, 82, "spark-3f2a.local", 10, 600, "#1d4ed8", family=MONO))
    out += f'<g transform="translate(170,132) rotate(-4)">{sticker}</g>'
    # magnifier over the sticker, enlarged copy
    out += card(t, 372, 76, 196, 160, rx=14)
    out += text(388, 99, "On the sticker", 12, 700, t["ink"])
    rows = [("Wi-Fi network", "spark-3f2a"), ("Wi-Fi password", "7kq4-m2vx"), ("Setup page", "spark-3f2a.local")]
    for i, (k, v) in enumerate(rows):
        out += text(388, 124 + i * 34, k, 9.5, 600, t["muted"])
        out += text(388, 138 + i * 34, v, 12, 700, t["accent"] if i == 2 else t["ink"], family=MONO)
    out += circle(336, 172, 22, t["accent_soft"], opacity=.35)
    out += circle(336, 172, 26, "none", stroke=t["accent"], sw=5) + path("M354,191 L366,205", stroke=t["accent"], sw=7)
    out += text(30, 38, "Find the sticker", 17, 700, t["ink"])
    out += text(30, 56, "It's on the Quick Start card in the box (yours has its own name and password).", 11, 500, t["muted"])
    return out


def hotspot(t):
    out = spark(t, 58, 168, 118)
    out += wifi(125, 128, 1.5, t["accent"])
    out += text(125, 238, "spark-3f2a", 11, 700, t["ink"], "middle", family=MONO)
    out += text(125, 252, "broadcasts its own Wi-Fi", 9.5, 500, t["muted"], "middle")
    lap, (sx, sy, sw, sh) = laptop(t, 300, 70, 230, screen="url(#brand)")
    out += lap
    # a desktop: a few icons on the wallpaper
    for i in range(3):
        out += rect(sx + 10, sy + 10 + i * 24, 16, 14, "#ffffff", rx=3, opacity=.55)
    # wifi list flyout
    fx, fy, fw = sx + sw - 146, sy + 8, 138
    out += card(t, fx, fy, fw, sh - 16, rx=6)
    out += text(fx + 10, fy + 16, "Wi-Fi", 9.5, 700, t["ink"])
    nets = [("spark-3f2a", True), ("Home-WiFi", False), ("Neighbor-5G", False)]
    for i, (n, hot) in enumerate(nets):
        ry = fy + 24 + i * 25
        if hot:
            out += rect(fx + 4, ry, fw - 8, 23, t["accent_soft"], rx=4)
        out += wifi(fx + 15, ry + 16, .38, t["accent"] if hot else t["muted"])
        out += text(fx + 26, ry + 15, n, 9, 700 if hot else 500, t["ink"])
    out += rect(fx + fw - 52, fy + 28, 44, 15, "url(#brand)", rx=4) + text(fx + fw - 30, fy + 38.5, "Connect", 7.5, 700, "#ffffff", "middle")
    out += cursor(fx + fw - 26, fy + 37, .8)
    out += path("M190,120 C230,96 262,96 296,112", stroke=t["accent"], sw=2, dash="3 5")
    out += text(30, 38, "Join the Spark's own Wi-Fi", 17, 700, t["ink"])
    out += text(30, 56, "Use the network name and password from the sticker.", 11, 500, t["muted"])
    return out


def setup_page(t):
    inner = ""
    x, y, w, h = 60, 66, 360, 190
    inner += text(x + 24, y + 58, "Set up your DGX Spark", 14, 700, t["ink"])
    fields = [("Username", "alice"), ("Password", "••••••••••"), ("Wi-Fi network", "Home-WiFi")]
    for i, (k, v) in enumerate(fields):
        fy = y + 72 + i * 36
        inner += text(x + 24, fy + 8, k, 9, 600, t["muted"])
        inner += rect(x + 24, fy + 12, 200, 18, t["surface2"], rx=4, stroke=t["stroke"])
        inner += text(x + 31, fy + 25, v, 10, 500, t["ink"])
    inner += path(f"M{x + 206},{y + 161} L{x + 210},{y + 165} L{x + 214},{y + 161}", stroke=t["muted"], sw=1.6)
    inner += rect(x + 250, y + 150, 86, 24, "url(#brand)", rx=6) + text(x + 293, y + 166, "Continue", 10.5, 700, "#ffffff", "middle")
    out = browser(t, x, y, w, h, "http://spark-3f2a.local", inner, secure=None)
    # notepad
    out += f'<g transform="translate(452,92) rotate(5)">{shadow(t, 0, 0, 112, 140, 4)}{rect(0, 0, 112, 140, "#fef9c3", rx=4, stroke="#eab308")}'
    for i in range(6):
        out += path(f"M10,{34 + i * 17} H102", stroke="#fde047", sw=1)
    out += text(12, 24, "Write these down!", 9.5, 700, "#854d0e")
    out += text(12, 50, "user: alice", 10, 600, "#422006", family=MONO)
    out += text(12, 67, "pass: ********", 10, 600, "#422006", family=MONO)
    out += text(12, 101, "You'll need them", 8.5, 600, "#854d0e")
    out += text(12, 114, "in a minute.", 8.5, 600, "#854d0e") + '</g>'
    out += text(30, 38, "Create your account", 17, 700, t["ink"])
    out += text(30, 56, "Pick a username and password, then choose your home Wi-Fi (or use a cable).", 11, 500, t["muted"])
    return out


def waiting(t):
    out = spark(t, 70, 150, 150)
    # progress ring
    cx, cy, r = 400, 142, 58
    out += circle(cx, cy, r, "none", stroke=t["surface3"], sw=12)
    out += path(f"M{cx},{cy - r} A{r},{r} 0 1 1 {cx - r * .87},{cy + r * .5}", stroke="url(#brandH)", sw=12)
    out += text(cx, cy - 2, "~10", 26, 700, t["ink"], "middle") + text(cx, cy + 16, "minutes", 11, 600, t["muted"], "middle")
    # update arrows near the spark
    out += circle(236, 112, 17, t["surface"], stroke=t["stroke"])
    out += path("M228,108 A9,9 0 0 1 244,110", stroke=t["accent"], sw=2.4) + path("M244,103 L244,110 L237,110", stroke=t["accent"], sw=2.4)
    out += path("M244,116 A9,9 0 0 1 228,114", stroke=t["accent"], sw=2.4) + path("M228,121 L228,114 L235,114", stroke=t["accent"], sw=2.4)
    out += pill(478, 104, "Updating", t["accent_soft"], t["accent"])
    out += pill(478, 130, "Restarting", t["surface3"], t["muted"])
    out += pill(478, 156, "Ready", t["surface3"], t["muted"])
    out += text(30, 38, "Let it finish", 17, 700, t["ink"])
    out += text(30, 56, "The Spark updates and restarts. Leave it plugged in — grab a coffee.", 11, 500, t["muted"])
    return out


def download(t):
    inner = ""
    x, y, w, h = 44, 66, 330, 186
    inner += text(x + 20, y + 56, "DGX Spark System Recovery", 12.5, 700, t["ink"])
    inner += text(x + 20, y + 74, "Recovery media for DGX Spark", 9.5, 500, t["muted"])
    inner += rect(x + 20, y + 88, w - 40, 36, t["surface2"], rx=6, stroke=t["stroke"])
    inner += text(x + 32, y + 104, "dgx-spark-recovery-image.tar.gz", 9.5, 600, t["ink"], family=MONO)
    inner += text(x + 32, y + 117, "Several GB", 8.5, 500, t["muted"])
    inner += rect(x + w - 104, y + 96, 72, 20, "url(#brand)", rx=5) + text(x + w - 68, y + 110, "Download", 9, 700, "#ffffff", "middle")
    inner += text(x + 20, y + 146, "Sign in with a free NVIDIA account first.", 9, 500, t["muted"])
    out = browser(t, x, y, w, h, "developer.nvidia.com", inner)
    # arrow into downloads folder
    out += path("M390,160 C420,160 430,150 452,150", stroke=t["accent"], sw=2.6, dash="4 5")
    out += path("M446,143 L455,150 L446,157", stroke=t["accent"], sw=2.6)
    fx, fy = 468, 108
    out += shadow(t, fx, fy, 104, 82, 8)
    out += path(f"M{fx},{fy + 8} A8,8 0 0 1 {fx + 8},{fy} H{fx + 38} L{fx + 48},{fy + 10} H{fx + 96} A8,8 0 0 1 {fx + 104},{fy + 18} V{fy + 74} A8,8 0 0 1 {fx + 96},{fy + 82} H{fx + 8} A8,8 0 0 1 {fx},{fy + 74} Z", fill="#fbbf24")
    out += rect(fx, fy + 18, 104, 64, "#fcd34d", rx=8)
    out += path(f"M{fx + 52},{fy + 32} V{fy + 60} M{fx + 42},{fy + 51} L{fx + 52},{fy + 61} L{fx + 62},{fy + 51}", stroke="#92400e", sw=3.2)
    out += text(fx + 52, fy + 102, "Downloads", 11, 700, t["ink"], "middle")
    out += text(30, 38, "Download the recovery file", 17, 700, t["ink"])
    out += text(30, 56, "From NVIDIA's website, then tell DSH where you saved it.", 11, 500, t["muted"])
    return out


def usb_writer(t):
    out = ""
    lap, (sx, sy, sw, sh) = laptop(t, 70, 74, 260)
    out += lap
    out += text(sx + 16, sy + 24, "Writing the recovery stick", 11.5, 700, t["ink"])
    out += text(sx + 16, sy + 40, "SanDisk Ultra 32 GB (E:)", 9.5, 500, t["muted"])
    out += rect(sx + 16, sy + 54, sw - 32, 9, t["surface3"], rx=4.5) + rect(sx + 16, sy + 54, (sw - 32) * .62, 9, "url(#brandH)", rx=4.5)
    out += text(sx + 16, sy + 78, "Copying files… 62%", 9.5, 600, t["ink"])
    out += rect(sx + 16, sy + 90, sw - 32, 30, t["warn_soft"], rx=5)
    out += warn_triangle(sx + 30, sy + 105, .55, t["warn"]) + text(sx + 42, sy + 109, "Everything on the stick is erased", 9, 700, t["ink"])
    # the stick, plugged into the laptop's side
    out += usb_stick(346, 219, 0, 1.25, left=True)
    out += text(456, 229, "FAT32", 13, 700, t["ink"], family=MONO)
    out += text(456, 245, "label BOOTME", 10, 600, t["muted"], family=MONO)
    out += check(470, 120, 16, t["ok"]) + text(494, 116, "Checked", 11, 700, t["ink"]) + text(494, 131, "before erasing", 10, 500, t["muted"])
    out += text(30, 38, "Make the recovery stick", 17, 700, t["ink"])
    out += text(30, 56, "DSH erases the stick and copies NVIDIA's files onto it — one click.", 11, 500, t["muted"])
    return out


def boot_menu(t):
    inner = ""
    mx, my, mw, mh = 40, 68, 300, 126
    ix, iy = mx + 14, my + 12
    inner += text(ix + 8, iy + 16, "GNU GRUB", 9, 700, "#94a3b8", family=MONO)
    inner += text(ix + 8, iy + 33, "DGX Spark Installation Options", 9.5, 700, "#e2e8f0", family=MONO)
    inner += rect(ix + 4, iy + 42, mw - 36, 17, "#e2e8f0", rx=2)
    inner += text(ix + 8, iy + 54.5, "> Install DGX OS for DGX Spark", 9.5, 700, "#0f172a", family=MONO)
    inner += text(ix + 8, iy + 73, "  Boot from the next device", 9.5, 500, "#94a3b8", family=MONO)
    inner += text(ix + 8, iy + 96, "Use ↑ ↓ and Enter", 8.5, 500, "#64748b", family=MONO)
    out = monitor(t, mx, my, mw, mh, inner)
    out += keyboard(t, 56, 224, 268, highlight=("Esc", "Del"))
    out += spark(t, 424, 212, 112)
    out += usb_stick(541, 218, 0, .56, "", left=True)
    out += badge(376, 102, 1) + text(392, 106.5, "Plug in the stick, keyboard", 11, 700, t["ink"])
    out += text(392, 120, "and screen", 11, 700, t["ink"])
    out += badge(376, 144, 2) + text(392, 148.5, "Power on, tap Esc or Del", 11, 700, t["ink"])
    out += badge(376, 178, 3) + text(392, 182.5, "Pick the USB, then Install", 11, 700, t["ink"])
    out += text(30, 38, "Start the Spark from the stick", 17, 700, t["ink"])
    out += text(30, 56, "With a keyboard and screen plugged into the Spark. Takes 25–30 minutes.", 11, 500, t["muted"])
    return out


def scanner(t):
    cx, cy = 172, 164
    out = ""
    for r, op in ((104, .10), (74, .16), (44, .24)):
        out += circle(cx, cy, r, t["accent"], opacity=op * .5) + circle(cx, cy, r, "none", stroke=t["accent"], sw=1.2, opacity=.5)
    out += path(f"M{cx},{cy} L{cx + 100},{cy - 30} A104,104 0 0 0 {cx + 70},{cy - 77} Z", fill=t["accent"], opacity=.28)
    lap, _ = laptop(t, cx - 30, cy - 22, 60)
    out += lap
    out += circle(cx + 62, cy - 58, 6, "#f59e0b") + circle(cx - 70, cy + 22, 5, t["muted"]) + circle(cx + 40, cy + 70, 5, t["muted"])
    # result cards
    rx0 = 318
    out += card(t, rx0, 82, 250, 64, rx=12)
    out += spark(t, rx0 + 14, 112, 46, led=True)
    out += text(rx0 + 76, 104, "DGX Spark at 192.168.1.42", 11.5, 700, t["ink"])
    out += text(rx0 + 76, 120, "spark-3f2a · remote login ready", 9.5, 500, t["muted"])
    out += pill(rx0 + 76, 126, "Use this one", "url(#brand)", "#ffffff", 8.5, h=15)
    out += card(t, rx0, 158, 250, 46, rx=12)
    out += circle(rx0 + 30, 181, 11, t["surface3"]) + text(rx0 + 30, 185, "PC", 8, 700, t["muted"], "middle")
    out += text(rx0 + 50, 178, "This PC — Ollama", 11, 700, t["ink"])
    out += text(rx0 + 50, 193, "qwen3:8b", 9.5, 500, t["muted"], family=MONO)
    out += text(rx0, 228, "Scanning 192.168.1.1 – 254 …", 9.5, 600, t["muted"])
    out += rect(rx0, 236, 250, 5, t["surface3"], rx=2.5) + rect(rx0, 236, 170, 5, "url(#brandH)", rx=2.5)
    out += text(30, 38, "Finding your Spark", 17, 700, t["ink"])
    out += text(30, 56, "DSH looks around your network — no IP addresses to type.", 11, 500, t["muted"])
    return out


def terminal(t):
    x, y, w, h = 44, 70, 340, 182
    out = shadow(t, x, y, w, h, 10) + rect(x, y, w, h, "#0f172a", rx=10)
    out += f'<path d="M{x},{y + 10} A10,10 0 0 1 {x + 10},{y} H{x + w - 10} A10,10 0 0 1 {x + w},{y + 10} V{y + 24} H{x} Z" fill="#1e293b"/>'
    out += circle(x + 14, y + 12, 3.4, "#f87171") + circle(x + 25, y + 12, 3.4, "#fbbf24") + circle(x + 36, y + 12, 3.4, "#34d399")
    out += text(x + w / 2, y + 15.5, "Installing Spark Swapper on spark-3f2a", 8.5, 600, "#94a3b8", "middle")
    lines = [
        ("#4ade80", "==> git 2.43.0 present."),
        ("#4ade80", "==> Cloning DGX-Spark-Swapper …"),
        ("#94a3b8", "[sudo] password for alice: (answered by DSH)"),
        ("#e2e8f0", "==> installing app to /opt/spark-swapper"),
        ("#e2e8f0", "==> generating a self-signed certificate"),
        ("#4ade80", "Spark Swapper is up: https://192.168.1.42:8999"),
    ]
    for i, (c, s) in enumerate(lines):
        out += text(x + 14, y + 44 + i * 19, s, 9, 500, c, family=MONO)
    out += rect(x + 14, y + 44 + 6 * 19 - 8, 7, 11, "#e2e8f0", opacity=.8)
    # right: key card
    kx, ky = 408, 88
    out += card(t, kx, ky, 160, 120, rx=12)
    out += circle(kx + 26, ky + 28, 13, t["accent_soft"])
    out += circle(kx + 22, ky + 28, 5, "none", stroke=t["accent"], sw=2.4) + path(f"M{kx + 27},{ky + 28} H{kx + 37} M{kx + 33},{ky + 28} V{ky + 33}", stroke=t["accent"], sw=2.4)
    out += text(kx + 46, ky + 25, "Spark's ID", 10.5, 700, t["ink"])
    out += text(kx + 46, ky + 38, "ssh-ed25519", 8.5, 500, t["muted"], family=MONO)
    out += text(kx + 14, ky + 64, "SHA256:x4Qp9Lk2…", 9.5, 600, t["ink"], family=MONO)
    out += text(kx + 14, ky + 78, "…Hs8fYt3mWq0", 9.5, 600, t["ink"], family=MONO)
    out += check(kx + 24, ky + 100, 8, t["ok"]) + text(kx + 38, ky + 104, "Remembered", 9.5, 700, t["ok"])
    out += text(30, 38, "Install Spark Swapper", 17, 700, t["ink"])
    out += text(30, 56, "DSH signs in to the Spark and runs the installer for you.", 11, 500, t["muted"])
    return out


def padlock(t):
    out = ""
    # left: public website
    out += card(t, 30, 74, 256, 170, rx=14)
    out += circle(66, 108, 17, t["ok_soft"]) + lock(66, 110, 1.1, t["ok"])
    out += text(92, 104, "A bank's website", 12, 700, t["ink"]) + text(92, 119, "bank.example", 9.5, 500, t["muted"], family=MONO)
    # authority stamp
    out += circle(88, 176, 30, "none", stroke=t["ok"], sw=2.4) + circle(88, 176, 24, "none", stroke=t["ok"], sw=1, opacity=.6)
    out += text(88, 173, "VOUCHED", 7.5, 700, t["ok"], "middle", spacing=.4) + text(88, 184, "BY A CA", 7.5, 700, t["ok"], "middle", spacing=.4)
    out += text(130, 164, "A public authority", 10, 600, t["ink"]) + text(130, 178, "signed its certificate,", 10, 500, t["muted"])
    out += text(130, 192, "so browsers know it.", 10, 500, t["muted"])
    out += pill(48, 214, "Padlock, no warning", t["ok_soft"], t["ok"], 9.5)
    # right: your spark
    out += card(t, 314, 74, 256, 170, rx=14)
    out += circle(350, 108, 17, t["warn_soft"]) + warn_triangle(350, 108, .75, t["warn"])
    out += text(376, 104, "Your Spark", 12, 700, t["ink"]) + text(376, 119, "192.168.1.42", 9.5, 500, t["muted"], family=MONO)
    out += spark(t, 346, 172, 66, led=True)
    out += text(430, 164, "It signed its own", 10, 600, t["ink"]) + text(430, 178, "certificate. Browsers", 10, 500, t["muted"])
    out += text(430, 192, "can't vouch for it.", 10, 500, t["muted"])
    out += pill(332, 214, "Warning — but it's yours", t["warn_soft"], t["warn"], 9.5)
    out += text(30, 38, "What's that padlock warning?", 17, 700, t["ink"])
    out += text(30, 56, "The connection is still encrypted. Only the \"who vouches for it\" part differs.", 11, 500, t["muted"])
    return out


def browser_warning(t):
    x, y, w, h = 50, 64, 380, 196
    inner = ""
    inner += warn_triangle(x + 40, y + 62, 1.25, t["bad"])
    inner += text(x + 64, y + 62, "Your connection isn't private", 13.5, 700, t["ink"])
    inner += text(x + 64, y + 79, "Attackers might be trying to steal your information", 9.5, 500, t["muted"])
    inner += text(x + 64, y + 92, "from 192.168.1.42.", 9.5, 500, t["muted"])
    inner += text(x + 64, y + 110, "NET::ERR_CERT_AUTHORITY_INVALID", 8.5, 500, t["faint"], family=MONO)
    inner += rect(x + 64, y + 124, 78, 22, t["surface"], rx=5, stroke=t["accent"], sw=1.6) + text(x + 103, y + 139, "Advanced", 10, 700, t["accent"], "middle")
    inner += rect(x + w - 124, y + 124, 100, 22, "url(#brand)", rx=5) + text(x + w - 74, y + 139, "Go back", 10, 700, "#ffffff", "middle")
    inner += text(x + 64, y + 170, "Continue to 192.168.1.42 (unsafe)", 10, 700, t["accent"])
    inner += path(f"M{x + 64},{y + 173} H{x + 254}", stroke=t["accent"], sw=1)
    out = browser(t, x, y, w, h, "https://192.168.1.42:8999", inner, secure="warn")
    out += cursor(x + 250, y + 162, 1)
    out += badge(x + 48, y + 135, 1, 9) + badge(x + 48, y + 166, 2, 9)
    out += card(t, 452, 88, 124, 128, rx=12)
    out += text(514, 110, "It's OK here", 12, 700, t["ink"], "middle")
    for i, s in enumerate(["Your own Spark", "On your network", "Fingerprint matches"]):
        out += check(470, 130 + i * 24, 7, t["ok"]) + text(482, 133.5 + i * 24, s, 9, 600, t["ink"])
    out += text(30, 38, "\"Your connection isn't private\"", 17, 700, t["ink"])
    out += text(30, 56, "Browsers say this about every self-signed certificate. For your Spark, click on through.", 11, 500, t["muted"])
    return out


def dsh_connected(t):
    x, y, w, h = 40, 64, 400, 196
    out = card(t, x, y, w, h, rx=12)
    out += f'<path d="M{x},{y + 12} A12,12 0 0 1 {x + 12},{y} H{x + 110} V{y + h} H{x + 12} A12,12 0 0 1 {x},{y + h - 12} Z" fill="{t["surface2"]}"/>'
    # logo mark
    out += rect(x + 12, y + 12, 18, 18, "url(#brand)", rx=5) + circle(x + 21, y + 20, 5, "none", stroke="#ffffff", sw=2)
    out += text(x + 36, y + 25, "DSH", 11, 700, t["ink"])
    for i, s in enumerate(["New chat", "Fix stock bug", "Spark setup"]):
        out += rect(x + 10, y + 42 + i * 22, 90, 17, t["accent_soft"] if i == 0 else "none", rx=4)
        out += text(x + 16, y + 54 + i * 22, s, 9, 600 if i == 0 else 500, t["ink"])
    cx0 = x + 124
    out += rect(cx0 + 150, y + 24, 118, 26, t["accent_soft"], rx=8) + text(cx0 + 158, y + 41, "Say hello to my Spark", 9, 600, t["ink"])
    out += rect(cx0, y + 62, 240, 44, t["surface2"], rx=8)
    out += text(cx0 + 10, y + 79, "Hello! Your DGX Spark is up and", 9.5, 500, t["ink"])
    out += text(cx0 + 10, y + 94, "ready — what shall we build first?", 9.5, 500, t["ink"])
    # composer
    out += rect(cx0, y + h - 44, 262, 30, t["surface"], rx=8, stroke=t["stroke"])
    out += text(cx0 + 10, y + h - 25, "Ask anything…", 9.5, 500, t["faint"])
    out += circle(cx0 + 248, y + h - 29, 9, "url(#brand)") + path(f"M{cx0 + 244},{y + h - 29} H{cx0 + 252} M{cx0 + 249},{y + h - 33} L{cx0 + 253},{y + h - 29} L{cx0 + 249},{y + h - 25}", stroke="#ffffff", sw=1.6)
    out += pill(cx0, y + h - 66, "● DGX Spark · qwen3.8-27b", t["ok_soft"], t["ok"], 8.5, h=16)
    # right: connection chain
    out += spark(t, 478, 150, 80)
    out += check(560, 110, 11, t["ok"])
    out += path("M444,164 C458,164 462,150 474,150", stroke=t["ok"], sw=2.4, dash="3 4")
    out += lock(524, 214, .9, t["ok"]) + text(534, 218, "pinned", 9.5, 700, t["ok"])
    out += text(30, 38, "DSH is connected", 17, 700, t["ink"])
    out += text(30, 56, "Chats now run on your Spark — privately, on your own network.", 11, 500, t["muted"])
    return out


def done(t):
    out = ""
    for i, (x, y, c, r) in enumerate([(80, 90, "#f59e0b", 5), (520, 80, "#22c55e", 4), (470, 230, "#06b6d4", 5), (120, 226, "#6366f1", 4),
                                      (300, 74, "#ec4899", 4), (560, 170, "#f59e0b", 3), (40, 170, "#06b6d4", 3)]):
        out += rect(x, y, r * 2.2, r, c, rx=1, extra=f'transform="rotate({(i * 37) % 90} {x} {y})"')
    lap, (sx, sy, sw, sh) = laptop(t, 92, 104, 130, screen="url(#brand)")
    out += lap + rect(sx + 10, sy + 10, sw - 20, 10, "#ffffff", rx=4, opacity=.6) + rect(sx + 10, sy + 26, sw * .5, 8, "#ffffff", rx=4, opacity=.4)
    out += spark(t, 382, 150, 132)
    out += path("M242,150 C280,130 330,130 372,146", stroke=t["ok"], sw=2.6, dash="4 5")
    out += circle(304, 136, 26, t["ok_soft"]) + check(304, 136, 18, t["ok"])
    items = [("Chat", "on your Spark"), ("/swap", "change the model"), ("Settings › Spark", "manage it")]
    for i, (a, b) in enumerate(items):
        px = 110 + i * 190
        label = f"{a} — {b}"
        w = len(label) * 9.5 * .58 + 24
        out += card(t, px - w / 2, 218, w, 26, rx=13)
        out += text(px, 235, label, 9.5, 600, t["ink"], "middle")
    out += text(300, 38, "You're all set", 18, 700, t["ink"], "middle")
    out += text(300, 56, "Your Spark, Spark Swapper and DSH are working together.", 11, 500, t["muted"], "middle")
    return out


# MARK: - Spark Swapper screenshots

def screenshot(t, png_path, crop, url, title):
    """Frame a crop of a Swapper screenshot in a generic browser window. The crop should have the
    window's aspect ratio (about 2.93:1); it is scaled to the window's width."""
    im = Image.open(png_path).convert("RGB")
    im = im.crop(crop)
    target_w = 1040
    im = im.resize((target_w, round(im.height * target_w / im.width)), Image.LANCZOS)
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=88, optimize=True)
    data = base64.b64encode(buf.getvalue()).decode()
    x, y, w, h = 40, 50, 520, 210
    iw, ih = w - 2, h - 32
    inner = (f'<clipPath id="shot"><path d="M{x + 1},{y + 31} H{x + w - 1} V{y + h - 10} A9,9 0 0 1 {x + w - 10},{y + h - 1} '
             f'H{x + 10} A9,9 0 0 1 {x + 1},{y + h - 10} Z"/></clipPath>'
             f'<image clip-path="url(#shot)" x="{x + 1}" y="{y + 31}" width="{iw}" height="{iw * im.height / im.width}" '
             f'preserveAspectRatio="xMidYMin slice" xlink:href="data:image/jpeg;base64,{data}"/>')
    out = browser(t, x, y, w, h, url, inner, secure="warn")
    out += text(30, 34, title, 16, 700, t["ink"])
    return out


PICTURES = {
    "need": need, "choice": choice, "cables": cables, "quickstart-card": quickstart_card, "hotspot": hotspot,
    "setup-page": setup_page, "waiting": waiting, "download": download, "usb-writer": usb_writer, "boot-menu": boot_menu,
    "scanner": scanner, "terminal": terminal, "padlock": padlock, "browser-warning": browser_warning,
    "dsh-connected": dsh_connected, "done": done,
}

SHOTS = {
    # name: (file, crop box in the 2480-wide original, url, title)
    "swapper-setup": ("00-setup.png", (560, 250, 1920, 714), "https://192.168.1.42:8999", "Spark Swapper: the one-time admin login"),
    "swapper-dashboard": ("02-dashboard.png", (280, 440, 2200, 1095), "https://192.168.1.42:8999", "Spark Swapper: pick a model to run"),
    "swapper-provisioning": ("04-provisioning.png", (240, 290, 2240, 972), "https://192.168.1.42:8999", "Spark Swapper: Provisioning"),
    "swapper-keys": ("03-credentials.png", (290, 1100, 2190, 1748), "https://192.168.1.42:8999", "Spark Swapper: Keys & connection"),
}


def icon_spark(t):
    """A small Spark for the scanner's result cards (44 x 28, rendered at 3x)."""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="44" height="28" viewBox="0 0 44 28">'
            f'{defs(t)}{spark(t, 2, 10, 36)}</svg>')


def main():
    for t in (LIGHT, DARK):
        base = "icon-spark" + ("" if t["name"] == "light" else "-dark")
        png = cairosvg.svg2png(bytestring=icon_spark(t).encode("utf-8"), output_width=132, output_height=84)
        Image.open(io.BytesIO(png)).save(os.path.join(HERE, base + ".png"), optimize=True)
    swapper = None
    if "--swapper" in sys.argv:
        swapper = sys.argv[sys.argv.index("--swapper") + 1]
    jobs = []
    for name, fn in PICTURES.items():
        for t in (LIGHT, DARK):
            jobs.append((name, t, frame(t, fn(t))))
    if swapper:
        for name, (file, crop, url, title) in SHOTS.items():
            for t in (LIGHT, DARK):
                jobs.append((name, t, frame(t, screenshot(t, os.path.join(swapper, "docs", "screenshots", file), crop, url, title))))
    only = [a for a in sys.argv[1:] if not a.startswith("--") and a != swapper]
    for name, t, svg in jobs:
        if only and name not in only:
            continue
        base = name + ("" if t["name"] == "light" else "-dark")
        # Screenshot frames embed a JPEG; keep their SVGs out of the repo (the PNG is the source of truth).
        if name not in SHOTS:
            with open(os.path.join(HERE, base + ".svg"), "w", encoding="utf-8") as f:
                f.write(svg)
        png = cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=W * SCALE, output_height=H * SCALE)
        im = Image.open(io.BytesIO(png)).convert("RGBA")
        # Palette PNGs are a fraction of the size and indistinguishable for flat art; screenshots stay full colour.
        if name not in SHOTS:
            im = im.quantize(colors=256, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE)
        im.save(os.path.join(HERE, base + ".png"), optimize=True)
        print("wrote", base)


if __name__ == "__main__":
    main()
