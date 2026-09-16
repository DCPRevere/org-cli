#!/usr/bin/env python3
"""Optional browser validation: pip install playwright; set CHROMIUM to its executable.
Uses only a disposable workspace. Usage: python tests/browser_smoke.py [org binary]
"""
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request
from playwright.sync_api import sync_playwright, expect

binary = str(Path(sys.argv[1] if len(sys.argv) > 1 else 'src/OrgCli/bin/Debug/net9.0/org').resolve())
with tempfile.TemporaryDirectory(prefix='org-board-test-') as workspace, tempfile.TemporaryFile(mode='w+') as log:
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    env = dict(os.environ, ORG_API_TOKEN='browser-test-token')
    process = subprocess.Popen([binary, 'serve', '-d', workspace, '--port', str(port)], env=env, stdout=log, stderr=log)
    base = f'http://127.0.0.1:{port}'
    try:
        for _ in range(200):
            try:
                with urllib.request.urlopen(base, timeout=1) as response:
                    assert response.status == 200
                    break
            except OSError:
                assert process.poll() is None
                time.sleep(.05)
        else:
            raise AssertionError('Server did not start')
        with sync_playwright() as pw:
            browser = pw.chromium.launch(executable_path=os.environ.get('CHROMIUM'), headless=True)
            page = browser.new_page(viewport={'width': 1400, 'height': 1000})
            errors = []
            page.on('pageerror', lambda error: errors.append(str(error)))
            page.goto(base)
            expect(page.locator('#message')).to_contain_text('access token')
            page.locator('#actor').fill('worker')
            page.locator('#token').fill('browser-test-token')
            page.get_by_role('button', name='Connect', exact=True).click()
            expect(page.get_by_role('button', name='+ New task')).to_be_enabled()
            page.get_by_role('button', name='+ New task').click()
            page.locator('#create-title').fill('Ship the release <script>alert(1)</script>')
            page.locator('#create-acceptance').fill('Tests pass; documentation matches behavior')
            page.locator('#create-project').fill('Release')
            page.locator('#create-text').fill('Coordinate implementation and review.')
            page.get_by_role('button', name='Create task', exact=True).click()
            expect(page.locator('#detail > .badge')).to_have_text('ready')
            expect(page.locator('#detail .detail-title')).to_contain_text('<script>')
            page.get_by_role('button', name='Claim task', exact=True).click()
            expect(page.locator('#detail > .badge')).to_have_text('working')
            page.locator('#evidence').fill('Regression suite passed; behavior inspected.')
            page.get_by_role('button', name='Submit work', exact=True).click()
            expect(page.locator('#detail > .badge')).to_have_text('review')
            # An editor changes requirements while work is awaiting review.
            task_file = Path(workspace, 'tasks.org')
            task_file.write_text(task_file.read_text().replace(
                'Tests pass; documentation matches behavior', 'Tests pass; direct edits are covered'))
            for _ in range(30):
                page.get_by_role('button', name='Refresh', exact=True).click()
                expect(page.locator('body')).to_have_attribute('aria-busy', 'false')
                if page.get_by_text('Requirements changed after submission.', exact=False).count():
                    break
                page.wait_for_timeout(200)
            expect(page.get_by_text('Requirements changed after submission.', exact=False)).to_be_visible()
            expect(page.get_by_role('button', name='Approve', exact=True)).to_have_count(0)
            page.locator('#actor').fill('reviewer')
            page.locator('#evidence').fill('Please address the edited requirements.')
            page.get_by_role('button', name='Request changes', exact=True).click()
            try:
                expect(page.locator('#detail > .badge')).to_have_text('ready')
            except AssertionError:
                print('Board message:', page.locator('#message').inner_text(), flush=True)
                raise
            page.locator('#actor').fill('worker')
            page.get_by_role('button', name='Claim task', exact=True).click()
            expect(page.locator('#detail > .badge')).to_have_text('working')
            page.locator('#evidence').fill('Revised requirements tested.')
            page.get_by_role('button', name='Submit work', exact=True).click()
            expect(page.locator('#detail > .badge')).to_have_text('review')
            page.locator('#actor').fill('reviewer')
            page.locator('#evidence').fill('Acceptance criteria verified independently.')
            page.get_by_role('button', name='Approve', exact=True).click()
            expect(page.locator('#detail > .badge')).to_have_text('done')
            page.locator('#status').select_option('done')
            expect(page.locator('#list .task')).to_have_count(1)
            task_file.write_text(task_file.read_text().replace('* DONE ', '* TODO '))
            for _ in range(30):
                page.get_by_role('button', name='Refresh', exact=True).click()
                expect(page.locator('body')).to_have_attribute('aria-busy', 'false')
                if page.locator('#list .task').count() == 0:
                    break
                page.wait_for_timeout(200)
            expect(page.locator('#list .task')).to_have_count(0)
            expect(page.get_by_text('This task is no longer in this view.', exact=False)).to_be_visible()
            screenshot = os.environ.get('ORG_BOARD_SCREENSHOT')
            if screenshot:
                page.screenshot(path=screenshot, full_page=True)
            page.set_viewport_size({'width': 390, 'height': 844})
            assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
            assert not errors, errors
            browser.close()
        assert ':TASK_REVIEWED_BY: reviewer' in Path(workspace, 'tasks.org').read_text()
        print('PASS browser: authentication, create, claim, submit, separate review, direct-edit invalidation and recovery, manual state changes, literal content, mobile layout')
    finally:
        process.send_signal(signal.SIGINT)
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        if process.returncode:
            log.seek(0)
            print(log.read(), file=sys.stderr)
