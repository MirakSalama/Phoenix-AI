"""Read a public HTTPS page through an isolated Chrome process over stdio MCP."""

import http.client
import ipaddress
import json
import re
import select
import socket
import socketserver
import ssl
import subprocess
import sys
import tempfile
import threading
import time
from contextlib import closing
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urljoin, urlsplit, urlunsplit


TOOL = {
    "name": "read_page",
    "description": "Read text from a public HTTPS URL using a temporary Chrome profile.",
    "inputSchema": {
        "type": "object",
        "properties": {"url": {"type": "string", "description": "The public HTTPS URL"}},
        "required": ["url"],
        "additionalProperties": False,
    },
}
CHROME_TIMEOUT = 16
NETWORK_LIMIT = 8 * 1024 * 1024
REDIRECT_STATUSES = {301, 302, 303, 307, 308}
MAX_REDIRECTS = 5
REDIRECT_TIMEOUT = 12


def public_target(url):
    if not isinstance(url, str) or len(url) > 2048 or any(ord(c) < 33 for c in url):
        raise ValueError("Enter a public HTTPS URL.")
    parts = urlsplit(url)
    if parts.scheme != "https" or parts.username is not None or parts.port not in (None, 443):
        raise ValueError("Chrome can read public HTTPS pages on the standard port.")
    if "\\" in url or not parts.hostname:
        raise ValueError("Enter a public HTTPS URL.")
    host = parts.hostname.rstrip(".").encode("idna").decode("ascii")
    if not re.fullmatch(r"(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}", host):
        raise ValueError("Enter a public website hostname.")
    if host.endswith((".local", ".localhost", ".internal", ".home.arpa")):
        raise ValueError("Private websites are unavailable to this Chrome tool.")
    addresses = []
    for info in socket.getaddrinfo(host, 443, socket.AF_INET, socket.SOCK_STREAM):
        address = ipaddress.ip_address(info[4][0])
        if address.is_global and not address.is_multicast and not address.is_reserved:
            addresses.append(info[4])
    if not addresses:
        raise ValueError("The website did not resolve to a public address.")
    return urlunsplit(("https", host, parts.path or "/", parts.query, "")), host, list(dict.fromkeys(addresses))


def redirect_location(url, host, addresses, context, deadline):
    parts = urlsplit(url)
    resource = urlunsplit(("", "", parts.path, parts.query, ""))
    for address in addresses:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        timeout = min(3, remaining)
        try:
            # Connect to a checked address while retaining the original hostname for TLS and HTTP.
            with socket.create_connection(address, timeout=timeout) as transport:
                with context.wrap_socket(transport, server_hostname=host) as tls:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        break
                    tls.settimeout(min(3, remaining))
                    with closing(http.client.HTTPSConnection(host, timeout=timeout, context=context)) as request:
                        request.sock = tls
                        request.request("GET", resource, headers={"Connection": "close"})
                        with request.getresponse() as response:
                            return response.getheader("Location") if response.status in REDIRECT_STATUSES else None
        except (OSError, http.client.HTTPException):
            continue
    raise OSError("The public website did not respond.")


def resolve_redirects(url):
    context = ssl.create_default_context()
    deadline = time.monotonic() + REDIRECT_TIMEOUT
    visited = set()
    for _ in range(MAX_REDIRECTS + 1):
        address, host, ips = public_target(url)
        if address in visited:
            raise ValueError("The website redirects in a loop.")
        visited.add(address)
        try:
            location = redirect_location(address, host, ips, context, deadline)
        except OSError:
            # Chrome can still try this validated origin with its own TLS and HTTP stack.
            return address, host, ips
        if not location:
            return address, host, ips
        url = urljoin(address, location)
    raise ValueError("The website redirects too many times.")


class OriginProxy(socketserver.ThreadingTCPServer):
    daemon_threads = True

    def __init__(self, host, addresses):
        self.authority = (host + ":443").encode("ascii")
        self.addresses = addresses
        self.remaining = NETWORK_LIMIT
        self.lock = threading.Lock()
        super().__init__(("127.0.0.1", 0), OriginTunnel)

    def budget(self, count):
        with self.lock:
            if self.remaining < count:
                return False
            self.remaining -= count
            return True


class OriginTunnel(socketserver.StreamRequestHandler):
    def handle(self):
        self.connection.settimeout(4)
        try:
            request = self.rfile.readline(4096).split()
            if len(request) != 3 or request[0] != b"CONNECT" or request[1].lower() != self.server.authority:
                self.wfile.write(b"HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n")
                return
            headers = 0
            while (line := self.rfile.readline(4096)) not in (b"", b"\r\n"):
                headers += len(line)
                if headers > 16384:
                    return
            for address in self.server.addresses:
                try:
                    remote = socket.create_connection(address, timeout=3)
                    break
                except OSError:
                    continue
            else:
                return
            with remote:
                self.wfile.write(b"HTTP/1.1 200 Connection established\r\n\r\n")
                deadline = time.monotonic() + CHROME_TIMEOUT
                sockets = (self.connection, remote)
                while time.monotonic() < deadline:
                    ready, _, _ = select.select(sockets, [], [], 2)
                    for source in ready:
                        data = source.recv(32768)
                        if not data or not self.server.budget(len(data)):
                            return
                        target = remote if source is self.connection else self.connection
                        target.sendall(data)
        except OSError:
            return


class PageText(HTMLParser):
    omitted = {"head", "script", "style", "noscript", "template", "svg", "iframe"}
    breaks = {"article", "br", "div", "h1", "h2", "h3", "li", "main", "p", "section", "tr"}

    def __init__(self):
        super().__init__()
        self.skip = []
        self.parts = []
        self.title = []

    def handle_starttag(self, tag, attrs):
        if tag in self.omitted or tag == "title" and self.skip == ["head"]:
            self.skip.append(tag)
        elif not self.skip and tag in self.breaks:
            self.parts.append("\n")

    def handle_endtag(self, tag):
        if self.skip and tag == self.skip[-1]:
            self.skip.pop()
        elif not self.skip and tag in self.breaks:
            self.parts.append("\n")

    def handle_data(self, data):
        if self.skip == ["head", "title"]:
            self.title.append(data)
        elif not self.skip:
            self.parts.append(data)

    def text(self):
        lines = [line for raw in "".join(self.parts).splitlines() if (line := " ".join(raw.split()))]
        title = " ".join("".join(self.title).split())
        if lines and title and title != lines[0]:
            lines.insert(0, title)
        return "\n".join(lines)


def dump_dom(chrome, url, proxy_port):
    with tempfile.TemporaryDirectory(prefix="phoenix-chrome-") as temp:
        profile = Path(temp) / "profile"
        preferences = profile / "Default" / "Preferences"
        preferences.parent.mkdir(parents=True)
        preferences.write_text(json.dumps({
            "profile": {"default_content_setting_values": {"javascript": 2, "automatic_downloads": 2, "popups": 2}},
            "download": {"default_directory": str(Path(temp) / "downloads"), "directory_upgrade": True},
        }), encoding="utf-8")
        output = Path(temp) / "page.html"
        with output.open("wb") as stream:
            result = subprocess.run([
                str(chrome), "--headless", "--dump-dom", "--no-first-run", "--no-default-browser-check",
                "--disable-background-networking", "--disable-component-update", "--disable-extensions",
                "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                "--proxy-bypass-list=<-loopback>", f"--proxy-server=http://127.0.0.1:{proxy_port}",
                f"--user-data-dir={profile}", "--timeout=10000", url,
            ], stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.DEVNULL,
                timeout=CHROME_TIMEOUT, check=False)
        if result.returncode != 0:
            raise ValueError("Chrome could not read this page.")
        with output.open("rb") as stream:
            return stream.read(2 * 1024 * 1024).decode("utf-8", errors="replace")


def read_page(chrome, url):
    address, host, ips = resolve_redirects(url)
    with OriginProxy(host, ips) as proxy:
        worker = threading.Thread(target=proxy.serve_forever, kwargs={"poll_interval": 0.1}, daemon=True)
        worker.start()
        try:
            document = dump_dom(chrome, address, proxy.server_address[1])
        finally:
            proxy.shutdown()
            worker.join()
    if "chrome-error://chromewebdata" in document or 'class="neterror"' in document:
        raise ValueError("Chrome could not reach this page on the approved hostname.")
    parser = PageText()
    parser.feed(document)
    text = parser.text()
    if not text:
        raise ValueError("Chrome found no readable text on this page.")
    return address, text[:2200]


def handle(chrome, method, params):
    if method == "initialize":
        return {"protocolVersion": "2025-11-25", "capabilities": {"tools": {}},
                "serverInfo": {"name": "phoenix-chrome", "version": "1.0.0"}}
    if method == "tools/list":
        return {"tools": [TOOL]}
    if method == "ping":
        return {}
    arguments = params.get("arguments")
    if method == "tools/call" and params.get("name") == TOOL["name"] and isinstance(arguments, dict) and set(arguments) == {"url"}:
        try:
            address, text = read_page(chrome, arguments["url"])
            return {"content": [{"type": "text", "text": text}],
                    "structuredContent": {"url": address, "text": text}}
        except (OSError, ValueError, subprocess.SubprocessError):
            return {"isError": True, "content": [{"type": "text",
                    "text": "Chrome could not read that public HTTPS page."}]}
    raise ValueError("Unsupported request")


def main():
    chrome = Path(sys.argv[1])
    if not chrome.is_file() or chrome.name.lower() != "chrome.exe":
        raise SystemExit("Google Chrome is unavailable.")
    for line in sys.stdin:
        try:
            request = json.loads(line)
            if not isinstance(request, dict) or "id" not in request:
                continue
            response = {"jsonrpc": "2.0", "id": request["id"]}
            try:
                response["result"] = handle(chrome, request["method"], request.get("params", {}))
            except (AttributeError, KeyError, ValueError):
                response["error"] = {"code": -32601, "message": "Unsupported request"}
            print(json.dumps(response), flush=True)
        except (ValueError, TypeError):
            continue


if __name__ == "__main__":
    main()
