#!/usr/bin/env python3
"""Real-process checks. Usage: python3 tests/server_smoke.py [path/to/org]."""
import json
import os
from pathlib import Path
import select
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor

BINARY = str(Path(sys.argv[1] if len(sys.argv) > 1 else "src/OrgCli/bin/Debug/net9.0/org").resolve())


def stop(process):
    if process.poll() is None:
        process.send_signal(signal.SIGINT)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def stdio(root):
    with tempfile.TemporaryFile(mode="w+") as errors:
        p = subprocess.Popen([BINARY, "mcp", "--stdio", "-d", root], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=errors, text=True, bufsize=1)
        sequence = 0
        def rpc(method, params=None):
            nonlocal sequence
            sequence += 1
            p.stdin.write(json.dumps(dict(jsonrpc="2.0", id=sequence, method=method, params=params or {})) + "\n")
            p.stdin.flush()
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                if select.select([p.stdout], [], [], max(0, deadline-time.monotonic()))[0]:
                    line = p.stdout.readline()
                    assert line, "MCP process closed stdout"
                    response = json.loads(line)  # Any console noise is a protocol failure.
                    if response.get("id") == sequence:
                        return response
            raise AssertionError("MCP response timed out")
        try:
            result = rpc("initialize", {"protocolVersion":"2025-11-25", "capabilities":{}, "clientInfo":{"name":"org-smoke", "version":"1"}})
            assert result["result"]["serverInfo"]["name"] == "org-cli", result
            cli_version = subprocess.check_output([BINARY, "--version"], text=True, timeout=10).strip().removeprefix("org ")
            assert result["result"]["serverInfo"]["version"] == cli_version, result
            p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
            p.stdin.flush()
            names = {t["name"] for t in rpc("tools/list")["result"]["tools"]}
            assert names == {"search","fetch","agenda","capture","append_note","update_task","related"}, names
            payload = {"request_id":str(uuid.uuid4()),"title":"Stdio task","state":"TODO"}
            result = rpc("tools/call", {"name":"capture","arguments":payload})["result"]
            assert not result["isError"], result
            data = result["structuredContent"]["data"]
            repeat = rpc("tools/call", {"name":"capture","arguments":payload})["result"]
            assert repeat["structuredContent"]["data"]["ref"] == data["ref"]
            result = rpc("tools/call", {"name":"update_task","arguments":{"ref":data["ref"],"expected_revision":data["revision"],"state":"DONE"}})["result"]
            assert not result["isError"], result
            assert "DONE" in result["structuredContent"]["data"]["text"]
            missing = rpc("tools/call", {"name":"fetch","arguments":{"ref":"id:missing"}})["result"]
            assert missing["isError"]
            p.stdin.close()
            assert p.wait(timeout=10) == 0
        except Exception:
            errors.seek(0)
            print(errors.read(), file=sys.stderr)
            raise
        finally:
            stop(p)
    print("PASS stdio: handshake, discovery, capture retry, task update, errors, EOF shutdown")


def http(root):
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    env = dict(os.environ, ORG_API_TOKEN="smoke-secret")
    with tempfile.TemporaryFile(mode="w+") as errors:
        p = subprocess.Popen([BINARY,"serve","--mcp","--port",str(port),"-d",root], stdout=errors,stderr=errors,env=env)
        base = f"http://127.0.0.1:{port}"
        def request(path, data=None, headers=None):
            headers = {"Authorization":"Bearer smoke-secret", **(headers or {})}
            body = None if data is None else json.dumps(data).encode()
            if body is not None:
                headers.setdefault("Content-Type", "application/json")
            req = urllib.request.Request(base+path, body, headers)
            try:
                with urllib.request.urlopen(req, timeout=10) as response:
                    return response.status, response.read().decode()
            except urllib.error.HTTPError as error:
                return error.code, error.read().decode()
        try:
            for attempt in range(100):
                assert p.poll() is None, "HTTP process exited"
                try:
                    if request("/health")[0] == 200: break
                except urllib.error.URLError:
                    time.sleep(0.05)
            else: raise AssertionError("HTTP startup timed out")
            assert request("/health",headers={"Authorization":"Bearer wrong"})[0] == 401
            assert request("/health",headers={"Origin":"https://evil.example"})[0] == 403
            assert request("/health",headers={"Host":"evil.example"})[0] == 403
            payload = {"request_id":str(uuid.uuid4()),"title":"Concurrent capture"}
            with ThreadPoolExecutor(max_workers=4) as pool:
                responses = list(pool.map(lambda _: request("/api/v1/capture",payload), range(4)))
            assert all(status == 200 for status,_ in responses), responses
            refs = {json.loads(body)["data"]["ref"] for _,body in responses}
            assert len(refs) == 1
            status,body = request("/api/v1/search",{"query":"Concurrent"})
            assert status == 200 and len(json.loads(body)["data"]["results"]) == 1, body
            init = dict(jsonrpc="2.0",id=1,method="initialize",params={"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"http-test","version":"1"}})
            status,body = request("/mcp",init,{"Accept":"application/json, text/event-stream"})
            assert status == 200 and "org-cli" in body, (status,body)
            stop(p)
            assert p.returncode == 0
        except Exception:
            errors.seek(0)
            print(errors.read(), file=sys.stderr)
            raise
        finally:
            stop(p)
    print("PASS HTTP: authentication, origin/host checks, concurrent capture retries, MCP, graceful shutdown")


with tempfile.TemporaryDirectory(prefix="org-server-smoke-") as root:
    stdio(root)
    http(root)
    result = subprocess.run([BINARY,"headlines","-d",root,"-f","json"],capture_output=True,text=True,timeout=10)
    assert result.returncode == 0 and "Concurrent capture" in result.stdout, result
    print("PASS ordinary CLI reads the same workspace with no server running")
