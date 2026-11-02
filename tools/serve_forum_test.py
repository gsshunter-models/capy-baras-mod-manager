"""Local-only download fixture for testing the embedded browser; never serves user files."""
from http.server import BaseHTTPRequestHandler, HTTPServer
from io import BytesIO
import sys, zipfile

buffer = BytesIO()
with zipfile.ZipFile(buffer, 'w', compression=zipfile.ZIP_DEFLATED) as package:
    package.writestr('ForumBrowserTest/terrain.hf', '9 2 0 1')
    package.writestr('ForumBrowserTest/desc', 'Forum Browser Test')
payload = buffer.getvalue()

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/track.zip':
            self.send_response(200)
            self.send_header('Content-Type', 'application/zip')
            self.send_header('Content-Disposition', 'attachment; filename="ForumBrowserTest.zip"')
            self.send_header('Content-Length', str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        else:
            body = b'<!doctype html><title>Forum download test</title><h1>Track download test</h1><a href="/track.zip">Download test track ZIP</a>'
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

HTTPServer(('127.0.0.1', int(sys.argv[1])), Handler).serve_forever()
