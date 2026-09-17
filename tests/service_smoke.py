#!/usr/bin/env python3
"""Service entrypoint and systemd unit validation using only temporary files.
Usage: python3 tests/service_smoke.py [path/to/org]
"""
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

binary = Path(sys.argv[1] if len(sys.argv) > 1 else 'src/OrgCli/bin/Debug/net9.0/org').resolve()
with tempfile.TemporaryDirectory(prefix='org-service-test-') as directory:
    root = Path(directory)
    workspace = root / 'notes with spaces $literal %h'
    workspace.mkdir()
    note = workspace / 'existing.org'
    original = '* TODO Service test\n:PROPERTIES:\n:ID: service-example\n:END:\n'
    note.write_text(original)
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    config = root / 'service.json'
    config.write_text(json.dumps(dict(ManagedBy='org-cli-service-v1', Directory=str(workspace), Port=port, Mcp=True, ReadOnly=True)))
    unrelated = root / 'unrelated-working-directory'
    unrelated.mkdir()
    (unrelated / 'appsettings.json').write_text('This is not an application configuration file')
    env = dict(os.environ, ORG_API_TOKEN='service-smoke-token')
    with (root / 'server.log').open('w+') as log:
        process = subprocess.Popen([str(binary), 'service', 'run', '--service-config', str(config)], env=env, stdout=log, stderr=log, cwd=unrelated)
        base = f'http://127.0.0.1:{port}'
        def request(path, payload=None, token=True):
            headers = {'Authorization': 'Bearer service-smoke-token'} if token else {}
            if payload is not None:
                headers['Content-Type'] = 'application/json'
            req = urllib.request.Request(base + path, None if payload is None else json.dumps(payload).encode(), headers)
            try:
                with urllib.request.urlopen(req, timeout=2) as response:
                    return response.status, json.loads(response.read())
            except urllib.error.HTTPError as error:
                return error.code, None
        try:
            for _ in range(100):
                assert process.poll() is None, 'Service entrypoint exited'
                try:
                    if request('/health')[0] == 200:
                        break
                except OSError:
                    time.sleep(.1)
            else:
                raise AssertionError('Service did not become ready')
            assert request('/health', token=False)[0] == 401
            init = dict(jsonrpc='2.0', id=1, method='initialize', params={'protocolVersion': '2025-11-25', 'capabilities': {}, 'clientInfo': {'name': 'service-smoke', 'version': '1'}})
            req = urllib.request.Request(base + '/mcp', json.dumps(init).encode(), {'Authorization': 'Bearer service-smoke-token', 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'})
            with urllib.request.urlopen(req, timeout=5) as response:
                assert response.status == 200 and 'org-cli' in response.read().decode()
            status, result = request('/api/v1/tasks', {'status': 'ready'})
            assert status == 200 and result['data']['total'] == 1, result
            status, _ = request('/api/v1/task_create', {'request_id': '6dbf5600-ecf5-4c25-8bfb-c959b7505db2', 'title': 'Forbidden', 'actor': 'test'})
            assert status in (403, 404), status
            assert note.read_text() == original
            assert not (workspace / 'tasks.org').exists()
        except Exception:
            log.flush()
            log.seek(0)
            print(log.read(), file=sys.stderr)
            raise
        finally:
            process.send_signal(signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        assert process.returncode == 0, process.returncode
    # Validate the shipped unit with a real executable location, without enabling it.
    unit = root / 'org-cli.service'
    unit.write_text(Path('packaging/systemd/org-cli.service').read_text().replace('/usr/bin/org', str(binary)))
    runtime = root / 'runtime'
    runtime.mkdir(mode=0o700)
    subprocess.run(['systemd-analyze', '--user', 'verify', str(unit)], env=dict(os.environ, XDG_RUNTIME_DIR=str(runtime)), check=True, timeout=15)
    # No user manager: fail before creating config or touching the workspace.
    fakebin = root / 'bin'
    fakebin.mkdir()
    fake = fakebin / 'systemctl'
    fake.write_text('#!/bin/sh\necho "No test user bus" >&2\nexit 1\n')
    fake.chmod(0o755)
    xdg = root / 'config'
    result = subprocess.run([str(binary), 'service', 'install', '--directory', str(workspace)], env=dict(env, PATH=str(fakebin) + os.pathsep + env['PATH'], XDG_CONFIG_HOME=str(xdg)), capture_output=True, text=True, timeout=15)
    assert result.returncode == 1 and 'No test user bus' in result.stderr, result
    assert not xdg.exists()
    assert note.read_text() == original
print('PASS service: saved settings, port, authentication, read-only workspace, graceful shutdown, systemd unit syntax, unavailable manager preservation')
