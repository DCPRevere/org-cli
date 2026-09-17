#!/usr/bin/env python3
"""Real-process checks. Usage: python3 tests/server_smoke.py [path/to/org]."""
import json
import os
from pathlib import Path
import queue
import threading
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing

BINARY = str(Path(sys.argv[1] if len(sys.argv) > 1 else "src/OrgCli/bin/Debug/net9.0/" + ("org.exe" if os.name == "nt" else "org")).resolve())


def stop(process):
    if process.poll() is None:
        if os.name == "nt":
            process.terminate()
        else:
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
        output = queue.Queue()
        def read_output():
            for line in p.stdout:
                output.put(line)
            output.put(None)
        threading.Thread(target=read_output, daemon=True).start()
        sequence = 0
        def rpc(method, params=None):
            nonlocal sequence
            sequence += 1
            p.stdin.write(json.dumps(dict(jsonrpc="2.0", id=sequence, method=method, params=params or {})) + "\n")
            p.stdin.flush()
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                try:
                    line = output.get(timeout=max(0.001, deadline-time.monotonic()))
                except queue.Empty:
                    break
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
            assert names == {"search","fetch","agenda","capture","append_note","update_task","related","tasks","task_create","task_update","task_action"}, names
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
            def tool(name, arguments):
                result = rpc("tools/call", {"name":name,"arguments":arguments})["result"]
                assert not result.get("isError"), result
                return result["structuredContent"]["data"]
            task = tool("task_create", {"request_id":str(uuid.uuid4()),"title":"Agent handoff","actor":"human","acceptance":"Verified output"})
            claim_id = str(uuid.uuid4())
            def transition(entry, actor, action, **fields):
                return tool("task_action", dict(ref=entry["ref"],expected_revision=entry["revision"],actor=actor,action=action,**fields))
            task = transition(task,"worker","claim",claim_id=claim_id)
            task = transition(task,"worker","submit",claim_id=claim_id,evidence="Tests pass")
            assert task["status"] == "review"
            task = transition(task,"reviewer","approve",evidence="Independently verified")
            assert task["status"] == "done"
            print("PASS MCP task workflow: create, claim, evidence, separate review")
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
            # Observe the index directly: requests must not be what refreshes it.
            database = Path(root) / ".org-index.db"
            def indexed(title):
                with closing(sqlite3.connect(database)) as connection:
                    return connection.execute("SELECT file FROM index_headlines WHERE title=?", (title,)).fetchall()
            def eventually(predicate):
                until = time.monotonic() + 15
                while time.monotonic() < until:
                    if predicate(): return
                    assert p.poll() is None, "Watcher process exited"
                    time.sleep(0.05)
                with closing(sqlite3.connect(database)) as connection:
                    observed = connection.execute("SELECT file, title FROM index_headlines").fetchall()
                raise AssertionError(f"Watcher did not refresh the index; observed: {observed!r}")
            folder = Path(root) / "watcher-folder"
            folder.mkdir()
            note = folder / "external.org"
            note.write_text("* watchbefore\n:PROPERTIES:\n:ID: watcher-note\n:END:\n")
            eventually(lambda: len(indexed("watchbefore")) == 1)
            previous = note.stat()
            replacement = folder / "save.tmp"
            replacement.write_text(note.read_text().replace("watchbefore", "watchafterx"))
            os.utime(replacement, ns=(previous.st_atime_ns, previous.st_mtime_ns))
            os.replace(replacement, note)
            eventually(lambda: len(indexed("watchafterx")) == 1 and not indexed("watchbefore"))
            renamed = Path(root) / "watcher-renamed"
            folder.rename(renamed)
            def renamed_projection():
                matches = indexed("watchafterx")
                return len(matches) == 1 and os.path.normcase(os.path.realpath(matches[0][0])) == os.path.normcase(os.path.realpath(renamed / "external.org"))
            eventually(renamed_projection)
            # Renaming away from .org must remove the old projection.
            (renamed / "external.org").rename(renamed / "external.txt")
            eventually(lambda: not indexed("watchafterx"))
            (renamed / "external.txt").unlink()
            renamed.rmdir()
            print("PASS watcher: background creation, atomic replacement with preserved mtime, directory rename, removal")
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
            status, body = request("/api/v1/task_create", {"request_id":str(uuid.uuid4()),"title":"Competing workers","actor":"human"})
            assert status == 200, body
            task = json.loads(body)["data"]
            def competing_claim(actor):
                return subprocess.run([BINARY,"task","claim",task["ref"],"--actor",actor,"--claim-id",str(uuid.uuid4()),"--expected-revision",task["revision"],"-d",root,"-f","json"],capture_output=True,text=True,env=env,timeout=20)
            with ThreadPoolExecutor(max_workers=2) as pool:
                claims = list(pool.map(competing_claim,["worker-one","worker-two"]))
            assert sorted(result.returncode for result in claims) == [0,1], [(r.returncode,r.stdout,r.stderr) for r in claims]
            winner = json.loads(next(r.stdout for r in claims if r.returncode == 0))["data"]
            assert winner["status"] == "working"
            print("PASS independent CLI workers: exactly one claim succeeds")
            init = dict(jsonrpc="2.0",id=1,method="initialize",params={"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"http-test","version":"1"}})
            status,body = request("/mcp",init,{"Accept":"application/json, text/event-stream"})
            assert status == 200 and "org-cli" in body, (status,body)
            stop(p)
            if os.name != "nt":
                assert p.returncode == 0
        except Exception:
            errors.seek(0)
            print(errors.read(), file=sys.stderr)
            raise
        finally:
            stop(p)
    print("PASS HTTP: authentication, origin/host checks, concurrent capture retries, MCP, process shutdown")


with tempfile.TemporaryDirectory(prefix="org-server-smoke-") as root:
    stdio(root)
    http(root)
    result = subprocess.run([BINARY,"headlines","-d",root,"-f","json"],capture_output=True,text=True,timeout=10)
    assert result.returncode == 0 and "Concurrent capture" in result.stdout, result
    print("PASS ordinary CLI reads the same workspace with no server running")
