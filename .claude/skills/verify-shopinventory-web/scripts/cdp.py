#!/usr/bin/env python3
"""Minimal Chrome DevTools Protocol client. Standard library only.

Why this exists rather than Playwright or Puppeteer
---------------------------------------------------
ShopInventory.Web cannot be driven by the usual tools:

  * Both browser MCP tools load a page in an iframe. The app sends
    `frame-ancestors 'self'` and `X-Frame-Options: SAMEORIGIN`, so the frame
    refuses to render and you get an error page, not the app.
  * Blazor Server renders with `prerender: false`, so `curl` returns the
    loading shell and never the page's real DOM.

Headless Chrome navigating *directly* is not framed, so the CSP does not apply
and the app renders normally. That is the whole trick.

Node is available but the repo is a .NET tree with no package.json at the root,
and `puppeteer-core` would drop a node_modules into it. Chrome is already
installed, so a small stdlib CDP client keeps the dependency count at zero.

Usage as a library:

    from cdp import Chrome
    with Chrome(headless=True) as c:
        c.goto("http://localhost:5051/login")
        c.wait_for("#username")
        c.type_into("#username", "admin")
        c.screenshot("shot.png")
"""

from __future__ import annotations

import base64
import json
import os
import shutil
import socket
import struct
import subprocess
import tempfile
import time
import urllib.request

CHROME_CANDIDATES = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    "/usr/bin/google-chrome",
    "/usr/bin/chromium",
]


def find_chrome() -> str:
    for p in CHROME_CANDIDATES:
        if os.path.exists(p):
            return p
    which = shutil.which("chrome") or shutil.which("google-chrome")
    if which:
        return which
    raise RuntimeError(
        "Chrome not found. Set CHROME_PATH, or install Chrome to the default location."
    )


class WebSocket:
    """Just enough RFC 6455 for CDP: text frames, client masking, no extensions."""

    def __init__(self, url: str, timeout: float = 30.0):
        assert url.startswith("ws://"), f"only ws:// supported, got {url}"
        rest = url[len("ws://"):]
        hostport, _, path = rest.partition("/")
        host, _, port = hostport.partition(":")
        self.sock = socket.create_connection((host, int(port or 80)), timeout=timeout)
        self.sock.settimeout(timeout)
        key = base64.b64encode(os.urandom(16)).decode()
        req = (
            f"GET /{path} HTTP/1.1\r\n"
            f"Host: {hostport}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        )
        self.sock.sendall(req.encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise RuntimeError("socket closed during websocket handshake")
            buf += chunk
        if b"101" not in buf.split(b"\r\n", 1)[0]:
            raise RuntimeError(f"websocket upgrade failed: {buf.split(chr(13).encode())[0]!r}")
        self._rest = buf.split(b"\r\n\r\n", 1)[1]

    def _recv_exact(self, n: int) -> bytes:
        out = self._rest[:n]
        self._rest = self._rest[n:]
        while len(out) < n:
            chunk = self.sock.recv(min(65536, n - len(out)))
            if not chunk:
                raise RuntimeError("socket closed mid-frame")
            out += chunk
        return out

    def send(self, text: str) -> None:
        payload = text.encode()
        header = bytearray([0x81])  # FIN + text opcode
        n = len(payload)
        if n < 126:
            header.append(0x80 | n)
        elif n < (1 << 16):
            header.append(0x80 | 126)
            header += struct.pack(">H", n)
        else:
            header.append(0x80 | 127)
            header += struct.pack(">Q", n)
        mask = os.urandom(4)
        header += mask
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        self.sock.sendall(bytes(header) + masked)

    def recv(self) -> str:
        chunks = []
        while True:
            b0, b1 = self._recv_exact(2)
            fin = b0 & 0x80
            opcode = b0 & 0x0F
            length = b1 & 0x7F
            if length == 126:
                length = struct.unpack(">H", self._recv_exact(2))[0]
            elif length == 127:
                length = struct.unpack(">Q", self._recv_exact(8))[0]
            data = self._recv_exact(length) if length else b""
            if opcode == 0x8:
                raise RuntimeError("websocket closed by peer")
            if opcode == 0x9:  # ping -> pong
                self.sock.sendall(b"\x8a\x80" + os.urandom(4))
                continue
            if opcode == 0xA:
                continue
            chunks.append(data)
            if fin:
                return b"".join(chunks).decode("utf-8", "replace")

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


class Chrome:
    def __init__(self, headless: bool = True, port: int = 0, width: int = 1440,
                 height: int = 1100, profile_dir: str | None = None, timeout: float = 30.0):
        self.port = port or _free_port()
        self.profile = profile_dir or tempfile.mkdtemp(prefix="verify-chrome-")
        self._owns_profile = profile_dir is None
        self.timeout = timeout
        args = [
            os.environ.get("CHROME_PATH") or find_chrome(),
            f"--remote-debugging-port={self.port}",
            f"--user-data-dir={self.profile}",
            f"--window-size={width},{height}",
            "--no-first-run", "--no-default-browser-check",
            "--disable-gpu", "--disable-extensions",
            "--disable-background-networking",
            "--disable-features=Translate,MediaRouter",
            "about:blank",
        ]
        if headless:
            args.insert(1, "--headless=new")
        self.proc = subprocess.Popen(
            args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
        )
        self.ws = WebSocket(self._page_ws_url(), timeout=timeout)
        self._id = 0
        self._send("Page.enable")
        self._send("Runtime.enable")

    def _page_ws_url(self) -> str:
        deadline = time.time() + self.timeout
        last = None
        while time.time() < deadline:
            try:
                raw = urllib.request.urlopen(
                    f"http://127.0.0.1:{self.port}/json/list", timeout=2
                ).read()
                for t in json.loads(raw):
                    if t.get("type") == "page" and t.get("webSocketDebuggerUrl"):
                        return t["webSocketDebuggerUrl"]
            except Exception as e:  # chrome not listening yet
                last = e
            time.sleep(0.2)
        raise RuntimeError(f"Chrome devtools port {self.port} never came up ({last})")

    def _send(self, method: str, **params):
        self._id += 1
        mid = self._id
        self.ws.send(json.dumps({"id": mid, "method": method, "params": params}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == mid:
                if "error" in msg:
                    raise RuntimeError(f"{method} failed: {msg['error']}")
                return msg.get("result", {})
            # everything else is an event; CDP interleaves them freely

    # ---- page operations -------------------------------------------------

    def goto(self, url: str, settle: float = 0.6) -> None:
        self._send("Page.navigate", url=url)
        self.wait_for_load()
        time.sleep(settle)  # Blazor circuit finishes wiring after load

    def wait_for_load(self, timeout: float | None = None) -> None:
        deadline = time.time() + (timeout or self.timeout)
        while time.time() < deadline:
            if self.eval("document.readyState") == "complete":
                return
            time.sleep(0.15)
        raise TimeoutError("page never reached readyState=complete")

    def eval(self, expression: str):
        r = self._send(
            "Runtime.evaluate", expression=expression,
            returnByValue=True, awaitPromise=True,
        )
        res = r.get("result", {})
        if r.get("exceptionDetails"):
            raise RuntimeError(f"JS error: {r['exceptionDetails'].get('text')}")
        return res.get("value")

    def wait_for(self, selector: str, timeout: float | None = None, visible: bool = True):
        """Wait for a selector. Returns True, or raises TimeoutError with the URL."""
        deadline = time.time() + (timeout or self.timeout)
        probe = (
            f"(() => {{ const e = document.querySelector({json.dumps(selector)});"
            f" if (!e) return false;"
            f" if (!{json.dumps(visible)}) return true;"
            f" const r = e.getBoundingClientRect();"
            f" return r.width > 0 && r.height > 0; }})()"
        )
        while time.time() < deadline:
            try:
                if self.eval(probe):
                    return True
            except RuntimeError:
                pass
            time.sleep(0.15)
        raise TimeoutError(
            f"selector {selector!r} not present after {timeout or self.timeout}s "
            f"(url={self.eval('location.href')})"
        )

    def type_into(self, selector: str, text: str) -> None:
        """Set a value the way Blazor notices: native setter + input/change events."""
        self.wait_for(selector)
        self.eval(
            "(() => { const el = document.querySelector(%s);"
            " const proto = el instanceof HTMLTextAreaElement"
            "   ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;"
            " Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, %s);"
            " el.dispatchEvent(new Event('input', {bubbles: true}));"
            " el.dispatchEvent(new Event('change', {bubbles: true}));"
            " return true; })()" % (json.dumps(selector), json.dumps(text))
        )

    def click(self, selector: str) -> None:
        self.wait_for(selector)
        self.eval(
            f"document.querySelector({json.dumps(selector)}).click()"
        )

    def text(self, selector: str = "body") -> str:
        return self.eval(
            f"(document.querySelector({json.dumps(selector)})||{{}}).innerText || ''"
        ) or ""

    def set_theme(self, dark: bool) -> None:
        """Nocturne keys off a `dark-theme` class on <body>/<html>."""
        self.eval(
            "(() => { const on = %s;"
            " for (const el of [document.body, document.documentElement]) {"
            "   if (!el) continue;"
            "   el.classList.toggle('dark-theme', on);"
            " } return true; })()" % ("true" if dark else "false")
        )
        time.sleep(0.25)

    def screenshot(self, path: str, full_page: bool = True) -> str:
        params = {"format": "png", "captureBeyondViewport": bool(full_page)}
        r = self._send("Page.captureScreenshot", **params)
        os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
        with open(path, "wb") as f:
            f.write(base64.b64decode(r["data"]))
        return path

    def console_errors(self) -> list:
        return self.eval("window.__verifyErrors || []") or []

    def install_error_trap(self) -> None:
        self.eval(
            "(() => { if (window.__verifyErrors) return true;"
            " window.__verifyErrors = [];"
            " window.addEventListener('error', e =>"
            "   window.__verifyErrors.push(String(e.message)));"
            " window.addEventListener('unhandledrejection', e =>"
            "   window.__verifyErrors.push('unhandled: ' + String(e.reason)));"
            " return true; })()"
        )

    # ---- lifecycle -------------------------------------------------------

    def close(self) -> None:
        try:
            self.ws.close()
        finally:
            try:
                self.proc.terminate()
                self.proc.wait(timeout=10)
            except Exception:
                self.proc.kill()
            if self._owns_profile:
                shutil.rmtree(self.profile, ignore_errors=True)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def _free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p
