#!/usr/bin/env python3

import argparse
import functools
import http.server
from pathlib import Path
import re
import ssl
from urllib.parse import unquote, urlsplit


FINGERPRINTED_ASSET = re.compile(r"\.[a-z0-9]{10}\.[^/]+$")


class CrossOriginIsolatedHandler(http.server.SimpleHTTPRequestHandler):
    def send_response(self, code: int, message: str | None = None) -> None:
        self.response_code = code
        super().send_response(code, message)

    def do_POST(self) -> None:
        self.send_error(404)

    def end_headers(self) -> None:
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cross-Origin-Resource-Policy", "same-origin")
        self.send_header("Origin-Agent-Cluster", "?1")
        path = unquote(urlsplit(self.path).path)
        if (self.response_code == 200 and
                FINGERPRINTED_ASSET.search(path) is not None):
            self.send_header(
                "Cache-Control",
                "public, max-age=31536000, immutable")
        else:
            self.send_header(
                "Cache-Control",
                "no-cache, max-age=0, must-revalidate")
            self.send_header("Pragma", "no-cache")
            self.send_header("Expires", "0")
        super().end_headers()


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Serve the Mia browser bundle over HTTPS.")
    parser.add_argument("--bind", default="0.0.0.0")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--directory", required=True)
    parser.add_argument("--cert", required=True)
    parser.add_argument("--key", required=True)
    args = parser.parse_args()

    handler = functools.partial(
        CrossOriginIsolatedHandler,
        directory=Path(args.directory).resolve())
    server = http.server.ThreadingHTTPServer((args.bind, args.port), handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(args.cert, args.key)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    print(
        f"Serving cross-origin-isolated HTTPS on "
        f"{args.bind}:{args.port} from {args.directory}",
        flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
