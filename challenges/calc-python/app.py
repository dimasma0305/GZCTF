#!/usr/bin/env python3
# "MathBox" — a calculator service used as an Attack & Defense target.
#
# GET /calc?expr=<python> evaluates the expression with eval(). That is the
# deliberate VULNERABILITY: it is arbitrary code execution. The intended
# attack (and the way the checker pulls the flag) is to read the rotating
# flag file through it, e.g.
#   /calc?expr=open(__import__('os').environ.get('GZCTF_FLAG_FILE','/flag')).read().strip()
# Defenders must sandbox the evaluator (or whitelist operations) while keeping
# /calc?expr=1+1 == "2" so the SLA stays green.
import os
import http.server
import urllib.parse

PORT = 5000


def flag_path():
    return os.environ.get("GZCTF_FLAG_FILE", "/flag")


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):  # quiet
        pass

    def _send(self, code, body):
        data = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        if u.path == "/health":
            return self._send(200, "ok")
        if u.path == "/calc":
            expr = q.get("expr", [""])[0]
            try:
                result = eval(expr, {"os": os, "open": open, "__import__": __import__})
                return self._send(200, str(result))
            except Exception as e:  # noqa: BLE001 — surface eval errors to the caller
                return self._send(500, "err: %s" % e)
        return self._send(200, "MathBox — /calc?expr=<python> ; /health")


if __name__ == "__main__":
    http.server.ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
