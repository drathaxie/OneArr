"""
Local fake upstream services for OneArr smoke tests. Standard library only, no network access.
  :9001  TMDb-shaped API        (fixtures)
  :9002  Torznab RSS indexer    (fixture XML; records the requested query)
  :9003  qBittorrent WebUI v2 + Radarr v3 read-only endpoint (records every request)
Run from this directory:  python3 fake_services.py
"""
import http.server
import json
import os
import threading
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = []


def fixture(name):
    with open(os.path.join(HERE, name), "rb") as f:
        return f.read()


MOVIE_DETAIL = json.dumps({"id": 603, "title": "The Matrix", "release_date": "1999-03-30", "overview": "fixture",
                           "imdb_id": "tt0133093", "poster_path": None}).encode()

CONTENT_BASE = os.environ.get("FAKE_CONTENT_BASE", "/data/torrents")
LOG_FILE = os.environ.get("FAKE_LOG", "")


def qbit_torrents():
    return json.dumps([
        {"hash": "abcdef0123456789abcdef0123456789abcdef01", "name": "The.Matrix.1999.2160p.BluRay.x265-GRP",
         "state": "uploading", "progress": 1,
         "content_path": CONTENT_BASE + "/onearr-movies/The.Matrix.1999.2160p.BluRay.x265-GRP.mkv",
         "category": "onearr-movies", "size": 25000000000}
    ]).encode()


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "fake/1.0"
    port_role = ""

    def log_message(self, fmt, *args):
        return  # keep output quiet; requests are captured in LOG for assertions

    def _send(self, code, body, ctype="application/json", headers=None):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n).decode("utf-8", "replace") if n else ""

    def do_GET(self):
        self._dispatch("GET", "")

    def do_POST(self):
        self._dispatch("POST", self._body())

    def _dispatch(self, method, body):
        url = urllib.parse.urlsplit(self.path)
        entry = {"role": self.port_role, "method": method, "path": url.path, "query": url.query, "body": body}
        LOG.append(entry)
        if LOG_FILE:
            with open(LOG_FILE, "a", encoding="utf-8") as f:
                f.write(json.dumps(entry) + "\n")
        if self.port_role == "tmdb":
            if url.path == "/3/search/movie":
                return self._send(200, fixture("tmdb_movie_search.json"))
            if url.path == "/3/movie/603":
                return self._send(200, MOVIE_DETAIL)
            return self._send(404, b'{"status_message":"not found"}')
        if self.port_role == "torznab":
            if url.path == "/api":
                return self._send(200, fixture("torznab_search.xml"), "application/xml")
            return self._send(404, b"not found", "text/plain")
        if self.port_role == "qbit" and url.path == "/api/v3/movie":
            # Radarr-shaped read endpoint served on the same fake host (ports are one fake "arr" box).
            if method != "GET":
                return self._send(405, b"read-only fixture", "text/plain")
            if self.headers.get("X-Api-Key") != "fixture-key":
                return self._send(401, b"Unauthorized", "text/plain")
            items = [{"title": "The Matrix", "year": 1999, "tmdbId": 603, "imdbId": "tt0133093",
                      "path": "/movies/The Matrix (1999)", "monitored": True},
                     {"title": "Unknown Film", "year": 2001, "monitored": False}]
            return self._send(200, json.dumps(items).encode())
        if self.port_role == "qbit":
            if url.path == "/api/v2/auth/login" and method == "POST" and "username=" in body:
                return self._send(200, b"Ok.", "text/plain", {"Set-Cookie": "SID=fakesid123; HttpOnly; path=/"})
            if "SID=fakesid123" not in self.headers.get("Cookie", ""):
                return self._send(403, b"Forbidden", "text/plain")
            if url.path == "/api/v2/torrents/add" and method == "POST":
                return self._send(200, b"Ok.", "text/plain")
            if url.path == "/api/v2/torrents/info":
                return self._send(200, qbit_torrents())
            return self._send(404, b"not found", "text/plain")
        return self._send(404, b"no route", "text/plain")


def serve(role, port):
    cls = type("H_" + role, (Handler,), {"port_role": role})
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", port), cls)
    httpd.serve_forever()


if __name__ == "__main__":
    for role, port in [("tmdb", 9001), ("torznab", 9002), ("qbit", 9003)]:
        threading.Thread(target=serve, args=(role, port), daemon=True).start()
    print("fake services listening on 9001 (tmdb), 9002 (torznab), 9003 (qbit + radarr)", flush=True)
    threading.Event().wait()
