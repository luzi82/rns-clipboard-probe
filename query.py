"""Local read-only probe client; one connection and one response per request."""
import argparse
import json
import socket
import sys


def query(request, *, port=47731, timeout=20):
    line = request if isinstance(request, str) else json.dumps(request, ensure_ascii=False)
    if '\n' in line or '\r' in line:
        raise ValueError('Send a single line; JSON strings must escape newlines.')
    payload = (line + '\n').encode('utf-8')
    if len(payload) > 65536:
        raise ValueError('Request exceeds 65536 UTF-8 bytes.')
    with socket.create_connection(('127.0.0.1', port), timeout=timeout) as client:
        client.settimeout(timeout)
        client.sendall(payload)
        reply = bytearray()
        while b'\n' not in reply:
            chunk = client.recv(65536)
            if not chunk:
                raise ConnectionError('Probe closed the connection before a complete reply.')
            reply.extend(chunk)
            if len(reply) > 4194304:
                raise ValueError('Response exceeds protocol limit.')
    raw = bytes(reply).split(b'\n', 1)[0].decode('utf-8')
    return raw if raw == 'missing' else json.loads(raw)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('request', nargs='?', help='Data path, one-line JSON, help, or screenshot(path). Defaults to stdin.')
    parser.add_argument('--port', type=int, default=47731)
    args = parser.parse_args()
    result = query(args.request if args.request is not None else sys.stdin.read().strip(), port=args.port)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    sys.exit(1 if isinstance(result, dict) and result.get('ok') is False else 0)
