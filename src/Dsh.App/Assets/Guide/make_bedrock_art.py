#!/usr/bin/env python3
"""Draws the AWS Bedrock setup guide's pictures, in the same style and palette as the DGX Spark
guide's (it reuses make_art.py's primitives, themes and renderer).

    PYTHONPATH=<cairosvg + Pillow> python3 make_bedrock_art.py [name ...]

Writes NAME.svg / NAME-dark.svg and 2x PNGs next to this script. No company logos are drawn — the
pictures name AWS and Bedrock in plain text only.
"""
import io
import os
import sys

import cairosvg
from PIL import Image

from make_art import (DARK, H, HERE, LIGHT, MONO, SCALE, W, badge, browser, caption, card, check, circle, cursor,
                      frame, laptop, lock, path, pill, rect, shadow, text, warn_triangle)


def cloud(t, cx, cy, s=1, fill=None, stroke=None):
    """A soft cloud, centred on (cx, cy)."""
    f = fill or t["surface"]
    d = (f"M{cx - 62 * s},{cy + 26 * s} "
         f"C{cx - 92 * s},{cy + 26 * s} {cx - 92 * s},{cy - 14 * s} {cx - 62 * s},{cy - 12 * s} "
         f"C{cx - 60 * s},{cy - 44 * s} {cx - 14 * s},{cy - 52 * s} {cx + 2 * s},{cy - 28 * s} "
         f"C{cx + 18 * s},{cy - 50 * s} {cx + 64 * s},{cy - 40 * s} {cx + 60 * s},{cy - 8 * s} "
         f"C{cx + 94 * s},{cy - 8 * s} {cx + 94 * s},{cy + 26 * s} {cx + 62 * s},{cy + 26 * s} Z")
    out = path(d, fill=t["shadow"], opacity=t["shadow_op"]).replace("<path", f'<path transform="translate(1,4)"', 1)
    out += path(d, fill=f, stroke=stroke or t["stroke"], sw=1.2)
    return out


def chip(t, x, y, label, tone="accent", w=None):
    colors = {"accent": (t["accent_soft"], t["accent"]), "ok": (t["ok_soft"], t["ok"]),
              "warn": (t["warn_soft"], t["warn"]), "muted": (t["surface2"], t["muted"])}
    bg, fg = colors[tone]
    return pill(x, y, label, bg, fg, 9.5, h=18)


def terminal_window(t, x, y, w, h, lines, title="Windows Terminal"):
    out = shadow(t, x, y, w, h, rx=8) + rect(x, y, w, h, t["screen"], rx=8)
    out += rect(x, y, w, 20, "#1f2937", rx=8) + rect(x, y + 12, w, 8, "#1f2937")
    out += text(x + 10, y + 14, title, 8.5, 600, "#94a3b8")
    for i, (s, color) in enumerate(lines):
        out += text(x + 10, y + 38 + i * 14, s, 9, 500, color, family=MONO)
    return out


# MARK: - pictures

def bedrock_welcome(t):
    out = ""
    # DSH on a laptop, left
    inner = rect(52, 116, 150, 12, t["accent_soft"], rx=3) + rect(52, 134, 110, 8, t["surface3"], rx=3)
    inner += rect(52, 148, 128, 8, t["surface3"], rx=3) + rect(52, 162, 92, 8, t["surface3"], rx=3)
    lap, _ = laptop(t, 40, 104, 176, inner)
    out += lap
    out += text(128, 250, "DSH on this PC", 11, 700, t["ink"], "middle")
    # the connection
    out += path("M226,150 C262,150 270,128 306,128", stroke=t["accent"], sw=2.4, dash="4 5")
    out += lock(266, 156, .9, t["ok"]) + text(278, 160, "signed in", 9, 700, t["ok"])
    # the cloud with models, right
    out += cloud(t, 440, 128, 1.45)
    out += text(440, 98, "Amazon Bedrock", 14, 700, t["ink"], "middle")
    out += text(440, 113, "in your AWS account", 9.5, 500, t["muted"], "middle")
    out += chip(t, 352, 124, "Claude", "accent") + chip(t, 408, 124, "Amazon Nova", "accent") + chip(t, 494, 124, "Llama", "accent")
    out += chip(t, 370, 148, "Mistral", "muted") + chip(t, 428, 148, "DeepSeek", "muted") + chip(t, 494, 148, "Qwen", "muted")
    out += caption(t, 440, 222, "Pay-as-you-go models", "billed to your AWS account")
    out += text(30, 38, "Use models from Amazon Bedrock", 17, 700, t["ink"])
    out += text(30, 56, "Sign in with your AWS account in the browser — DSH sets up everything else.", 11, 500, t["muted"])
    return out


def aws_cli_install(t):
    out = ""
    # installer dialog
    x, y, w, h = 40, 72, 330, 178
    out += card(t, x, y, w, h, rx=10)
    out += rect(x, y, w, 26, t["surface2"], rx=10) + rect(x, y + 16, w, 10, t["surface2"])
    out += text(x + 12, y + 17, "AWS Command Line Interface v2 Setup", 9.5, 600, t["ink"])
    out += text(x + 18, y + 52, "Installing the AWS CLI", 13, 700, t["ink"])
    out += text(x + 18, y + 68, "DSH uses it to sign you in and talk to your account.", 9.5, 500, t["muted"])
    out += rect(x + 18, y + 86, w - 36, 10, t["surface3"], rx=5) + rect(x + 18, y + 86, (w - 36) * .68, 10, "url(#brandH)", rx=5)
    out += text(x + 18, y + 112, "Copying files…", 9.5, 500, t["muted"])
    out += rect(x + w - 92, y + h - 36, 74, 22, t["surface"], rx=5, stroke=t["stroke"]) + text(x + w - 55, y + h - 21, "Cancel", 9.5, 600, t["muted"], "middle")
    # Windows asks first
    out += card(t, 400, 80, 176, 92, rx=10)
    out += rect(416, 96, 22, 26, "url(#brand)", rx=4)
    out += path("M427,100 L435,104 V111 C435,116 431,119 427,121 C423,119 419,116 419,111 V104 Z", fill="#ffffff", opacity=.9)
    out += text(446, 106, "Windows asks:", 9.5, 700, t["ink"]) + text(446, 120, "allow the installer?", 9.5, 500, t["muted"])
    out += rect(416, 136, 70, 22, "url(#brand)", rx=5) + text(451, 151, "Yes", 10, 700, "#ffffff", "middle")
    out += rect(494, 136, 66, 22, t["surface"], rx=5, stroke=t["stroke"]) + text(527, 151, "No", 10, 600, t["muted"], "middle")
    out += cursor(468, 146, .9)
    # checks
    for i, s in enumerate(["Downloaded from Amazon", "Signature checked", "Installed"]):
        out += check(412, 194 + i * 20, 7, t["ok"]) + text(425, 197.5 + i * 20, s, 9.5, 600, t["ink"])
    out += text(30, 38, "One tool first: the AWS CLI", 17, 700, t["ink"])
    out += text(30, 56, "DSH downloads Amazon's official installer, checks its signature and runs it.", 11, 500, t["muted"])
    return out


def aws_signin(t):
    out = ""
    x, y, w, h = 34, 68, 330, 188
    inner = ""
    inner += text(x + w / 2, y + 60, "Sign in", 15, 700, t["ink"], "middle")
    inner += text(x + w / 2, y + 76, "with your AWS account", 10, 500, t["muted"], "middle")
    inner += rect(x + 60, y + 88, w - 120, 20, t["surface"], rx=5, stroke=t["stroke"]) + text(x + 70, y + 102, "you@example.com", 9, 500, t["faint"])
    inner += rect(x + 60, y + 114, w - 120, 20, t["surface"], rx=5, stroke=t["stroke"]) + text(x + 70, y + 128, "••••••••••", 9, 500, t["faint"])
    inner += rect(x + 60, y + 144, w - 120, 24, "url(#brand)", rx=5) + text(x + w / 2, y + 160, "Sign in", 10.5, 700, "#ffffff", "middle")
    out += browser(t, x, y, w, h, "signin.aws.amazon.com", inner, secure="lock")
    out += cursor(x + w / 2 + 40, y + 154, 1)
    # terminal on the right: the CLI waits, then confirms
    out += terminal_window(t, 386, 86, 190, 104, [
        ("> aws login", "#e2e8f0"),
        ("Opening your browser…", "#94a3b8"),
        ("Waiting for sign-in…", "#94a3b8"),
        ("Updated profile dsh-bedrock", "#4ade80"),
    ], "AWS CLI (DSH runs it)")
    out += check(566, 208, 10, t["ok"])
    out += text(386, 214, "Signed in to AWS", 11, 700, t["ink"])
    out += text(386, 229, "no keys to copy or paste", 9.5, 500, t["muted"])
    out += text(30, 38, "Sign in with your browser", 17, 700, t["ink"])
    out += text(30, 56, "The same sign-in as the AWS website. DSH never sees your password.", 11, 500, t["muted"])
    return out


def bedrock_models(t):
    out = ""
    rows = [
        ("Claude Sonnet", "Anthropic", "Ready", "ok"),
        ("Claude Opus", "Anthropic", "One-time form", "warn"),
        ("Amazon Nova Pro", "Amazon", "Ready", "ok"),
        ("Llama 4 Maverick", "Meta", "Accept terms", "warn"),
    ]
    x, y, w = 40, 70, 380
    out += card(t, x, y, w, 186, rx=12)
    out += text(x + 16, y + 24, "Models in US East (N. Virginia)", 11, 700, t["ink"])
    for i, (name, maker, status, tone) in enumerate(rows):
        ry = y + 38 + i * 36
        sel = i == 0
        out += rect(x + 10, ry, w - 20, 30, t["accent_soft"] if sel else t["surface2"], rx=7,
                    stroke=t["accent"] if sel else None, sw=1.4)
        out += circle(x + 28, ry + 15, 8, "url(#brand)" if sel else t["surface3"])
        out += text(x + 44, ry + 13, name, 10.5, 700, t["ink"]) + text(x + 44, ry + 25, maker, 8.5, 500, t["muted"])
        out += chip(t, x + w - 128, ry + 6, status, tone)
    out += card(t, 440, 92, 136, 128, rx=12)
    out += text(508, 114, "DSH checks", 11.5, 700, t["ink"], "middle")
    for i, s in enumerate(["Is it in this Region?", "Terms accepted?", "Form on file?", "You're allowed?"]):
        out += check(456, 132 + i * 21, 6.5, t["ok"]) + text(468, 135.5 + i * 21, s, 8.8, 600, t["ink"])
    out += text(30, 38, "Pick a model", 17, 700, t["ink"])
    out += text(30, 56, "DSH shows what each one needs before you can use it — and does it for you.", 11, 500, t["muted"])
    return out


def bedrock_terms(t):
    out = ""
    x, y, w, h = 40, 70, 340, 186
    out += card(t, x, y, w, h, rx=12)
    out += text(x + 18, y + 28, "Llama 4 Maverick · Meta", 12.5, 700, t["ink"])
    out += text(x + 18, y + 44, "Sold through AWS Marketplace", 9.5, 500, t["muted"])
    out += rect(x + 18, y + 58, w - 36, 54, t["surface2"], rx=8)
    out += text(x + 30, y + 78, "Pricing", 9.5, 700, t["ink"]) + text(x + 100, y + 78, "pay per use, on your AWS bill", 9.5, 500, t["muted"])
    out += text(x + 30, y + 98, "Licence", 9.5, 700, t["ink"]) + text(x + 100, y + 98, "Read the model's terms", 9.5, 700, t["accent"])
    out += path(f"M{x + 100},{y + 101} H{x + 214}", stroke=t["accent"], sw=1)
    out += rect(x + 18, y + 124, 14, 14, "url(#brand)", rx=3) + path(f"M{x + 21},{y + 131} L{x + 24.5},{y + 134.5} L{x + 29.5},{y + 127.5}", stroke="#ffffff", sw=1.8)
    out += text(x + 40, y + 135, "I accept these terms", 10, 600, t["ink"])
    out += rect(x + w - 150, y + h - 38, 132, 24, "url(#brand)", rx=6) + text(x + w - 84, y + h - 22, "Accept and enable", 10, 700, "#ffffff", "middle")
    out += cursor(x + w - 28, y + h - 24, .9)
    # result
    out += card(t, 404, 104, 172, 112, rx=12)
    out += check(490, 140, 14, t["ok"])
    out += text(490, 174, "Enabled", 12.5, 700, t["ink"], "middle")
    out += text(490, 190, "ready in a few minutes", 9.5, 500, t["muted"], "middle")
    out += text(30, 38, "Some models have terms to accept", 17, 700, t["ink"])
    out += text(30, 56, "You see the price and the licence first. One click accepts them for your account.", 11, 500, t["muted"])
    return out


def bedrock_usecase(t):
    out = ""
    x, y, w, h = 40, 70, 340, 186
    out += card(t, x, y, w, h, rx=12)
    out += text(x + 18, y + 28, "Tell Anthropic how you'll use Claude", 12.5, 700, t["ink"])
    fields = [("Company or name", "Acme Studio"), ("Website", "https://acme.example"), ("What for", "Coding help for our team")]
    for i, (label, value) in enumerate(fields):
        fy = y + 44 + i * 34
        out += text(x + 18, fy + 8, label, 8.5, 600, t["muted"])
        out += rect(x + 18, fy + 12, w - 36, 18, t["surface"], rx=4, stroke=t["stroke"]) + text(x + 26, fy + 25, value, 9, 500, t["ink"])
    out += rect(x + w - 118, y + h - 34, 100, 22, "url(#brand)", rx=6) + text(x + w - 68, y + h - 19, "Send", 10, 700, "#ffffff", "middle")
    out += card(t, 404, 96, 172, 124, rx=12)
    out += text(490, 120, "Once per account", 11.5, 700, t["ink"], "middle")
    for i, s in enumerate(["Asked by Anthropic", "Sent through AWS", "Covers every Claude"]):
        out += check(420, 146 + i * 24, 7, t["ok"]) + text(433, 149.5 + i * 24, s, 9.3, 600, t["ink"])
    out += text(30, 38, "Claude asks a few questions, once", 17, 700, t["ink"])
    out += text(30, 56, "A short form Anthropic requires before an AWS account first uses Claude.", 11, 500, t["muted"])
    return out


def bedrock_connected(t):
    out = ""
    x, y, w, h = 40, 64, 400, 196
    out += card(t, x, y, w, h, rx=12)
    out += f'<path d="M{x},{y + 12} A12,12 0 0 1 {x + 12},{y} H{x + 110} V{y + h} H{x + 12} A12,12 0 0 1 {x},{y + h - 12} Z" fill="{t["surface2"]}"/>'
    out += rect(x + 12, y + 12, 18, 18, "url(#brand)", rx=5) + circle(x + 21, y + 20, 5, "none", stroke="#ffffff", sw=2)
    out += text(x + 36, y + 25, "DSH", 11, 700, t["ink"])
    for i, s in enumerate(["New chat", "Refactor API", "Write tests"]):
        out += rect(x + 10, y + 42 + i * 22, 90, 17, t["accent_soft"] if i == 0 else "none", rx=4)
        out += text(x + 16, y + 54 + i * 22, s, 9, 600 if i == 0 else 500, t["ink"])
    cx0 = x + 124
    out += rect(cx0 + 150, y + 24, 118, 26, t["accent_soft"], rx=8) + text(cx0 + 160, y + 41, "Hello from Bedrock?", 9, 600, t["ink"])
    out += rect(cx0, y + 62, 240, 44, t["surface2"], rx=8)
    out += text(cx0 + 10, y + 79, "Hello! I'm running on Amazon", 9.5, 500, t["ink"])
    out += text(cx0 + 10, y + 94, "Bedrock — what shall we build?", 9.5, 500, t["ink"])
    out += rect(cx0, y + h - 44, 262, 30, t["surface"], rx=8, stroke=t["stroke"])
    out += text(cx0 + 10, y + h - 25, "Ask anything…", 9.5, 500, t["faint"])
    out += circle(cx0 + 248, y + h - 29, 9, "url(#brand)")
    out += path(f"M{cx0 + 244},{y + h - 29} H{cx0 + 252} M{cx0 + 249},{y + h - 33} L{cx0 + 253},{y + h - 29} L{cx0 + 249},{y + h - 25}", stroke="#ffffff", sw=1.6)
    out += pill(cx0, y + h - 66, "● Amazon Bedrock · Claude Sonnet", t["ok_soft"], t["ok"], 8.5, h=16)
    out += cloud(t, 516, 146, .7)
    out += text(516, 140, "Bedrock", 10.5, 700, t["ink"], "middle")
    out += text(516, 153, "your account", 8.5, 500, t["muted"], "middle")
    out += check(560, 116, 10, t["ok"])
    out += path("M444,158 C452,158 454,156 460,156", stroke=t["ok"], sw=2.4, dash="3 4")
    out += lock(488, 196, .8, t["ok"]) + text(498, 200, "stays signed in", 9, 700, t["ok"])
    out += text(30, 38, "DSH is connected", 17, 700, t["ink"])
    out += text(30, 56, "Chats run on Bedrock in your AWS account. Pay only for what you use.", 11, 500, t["muted"])
    return out


PICTURES = {
    "bedrock-welcome": bedrock_welcome,
    "aws-cli-install": aws_cli_install,
    "aws-signin": aws_signin,
    "bedrock-models": bedrock_models,
    "bedrock-terms": bedrock_terms,
    "bedrock-usecase": bedrock_usecase,
    "bedrock-connected": bedrock_connected,
}


def main():
    only = [a for a in sys.argv[1:] if not a.startswith("--")]
    for name, fn in PICTURES.items():
        if only and name not in only:
            continue
        for t in (LIGHT, DARK):
            svg = frame(t, fn(t))
            base = name + ("" if t["name"] == "light" else "-dark")
            with open(os.path.join(HERE, base + ".svg"), "w", encoding="utf-8") as f:
                f.write(svg)
            png = cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=W * SCALE, output_height=H * SCALE)
            im = Image.open(io.BytesIO(png)).convert("RGBA")
            im = im.quantize(colors=256, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE)
            im.save(os.path.join(HERE, base + ".png"), optimize=True)
            print("wrote", base)


if __name__ == "__main__":
    main()
