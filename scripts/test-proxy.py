#!/usr/bin/env python3
"""Proxy kecil untuk tes Zevn Shell: SOCKS5 + HTTP CONNECT, keduanya wajib login.

    python3 scripts/test-proxy.py <port-socks> <port-http> <user> <password> <log> [alamat-bind]

Mendengar di 127.0.0.1 (atau [alamat-bind], mis. untuk tes dari VM). Setiap koneksi yang diteruskan dicatat ke <log>
("socks host:port" / "http host:port"), supaya tes bisa membuktikan trafik lewat proxy.
"""
import base64
import socket
import struct
import sys
import threading

SOCKS_PORT, HTTP_PORT, USER, PASSWORD, LOG = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3], sys.argv[4], sys.argv[5]
BIND = sys.argv[6] if len(sys.argv) > 6 else "127.0.0.1"
lock = threading.Lock()


def log(line):
    with lock, open(LOG, "a") as f:
        f.write(line + "\n")


def recv_exact(s, n):
    b = b""
    while len(b) < n:
        c = s.recv(n - len(b))
        if not c:
            raise ConnectionError("closed")
        b += c
    return b


def pipe(a, b):
    def go(src, dst):
        try:
            while True:
                d = src.recv(65536)
                if not d:
                    break
                dst.sendall(d)
        except OSError:
            pass
        finally:
            try:
                dst.shutdown(socket.SHUT_WR)
            except OSError:
                pass
    t = threading.Thread(target=go, args=(b, a), daemon=True)
    t.start()
    go(a, b)
    t.join()
    a.close()
    b.close()


def socks(c):
    ver, n = recv_exact(c, 2)
    methods = recv_exact(c, n)
    if 2 not in methods:
        c.sendall(b"\x05\xff")
        return c.close()
    c.sendall(b"\x05\x02")
    _, ul = recv_exact(c, 2)
    u = recv_exact(c, ul).decode()
    pl = recv_exact(c, 1)[0]
    p = recv_exact(c, pl).decode()
    if (u, p) != (USER, PASSWORD):
        c.sendall(b"\x01\x01")
        return c.close()
    c.sendall(b"\x01\x00")
    _, cmd, _, atyp = recv_exact(c, 4)
    if atyp == 1:
        host = socket.inet_ntoa(recv_exact(c, 4))
    elif atyp == 3:
        host = recv_exact(c, recv_exact(c, 1)[0]).decode()
    else:
        host = socket.inet_ntop(socket.AF_INET6, recv_exact(c, 16))
    port = struct.unpack(">H", recv_exact(c, 2))[0]
    try:
        r = socket.create_connection((host, port), timeout=10)
        r.settimeout(None)
    except OSError:
        c.sendall(b"\x05\x05\x00\x01" + b"\0" * 6)
        return c.close()
    c.sendall(b"\x05\x00\x00\x01" + b"\0" * 6)
    log(f"socks {host}:{port}")
    pipe(c, r)


def http(c):
    head = b""
    while b"\r\n\r\n" not in head:
        d = c.recv(1)
        if not d:
            return c.close()
        head += d
    lines = head.decode().split("\r\n")
    method, target, _ = lines[0].split(" ", 2)
    auth = next((l.split(":", 1)[1].strip() for l in lines if l.lower().startswith("proxy-authorization:")), "")
    if auth != "Basic " + base64.b64encode(f"{USER}:{PASSWORD}".encode()).decode():
        c.sendall(b"HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic\r\n\r\n")
        return c.close()
    host, port = target.rsplit(":", 1)
    try:
        r = socket.create_connection((host.strip("[]"), int(port)), timeout=10)
        r.settimeout(None)
    except OSError:
        c.sendall(b"HTTP/1.1 502 Bad Gateway\r\n\r\n")
        return c.close()
    c.sendall(b"HTTP/1.1 200 Connection established\r\n\r\n")
    log(f"http {host}:{port}")
    pipe(c, r)


def serve(port, handler):
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((BIND, port))
    s.listen(16)
    while True:
        c, _ = s.accept()
        threading.Thread(target=lambda: _safe(handler, c), daemon=True).start()


def _safe(h, c):
    try:
        h(c)
    except Exception:
        c.close()


threading.Thread(target=serve, args=(SOCKS_PORT, socks), daemon=True).start()
serve(HTTP_PORT, http)
