#!/usr/bin/env python3
"""Real browser checks for file-faithful workflows; all files/config are disposable."""
from datetime import datetime, timezone
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
with tempfile.TemporaryDirectory(prefix='org-workflow-board-') as temp, tempfile.TemporaryFile(mode='w+') as log:
    root = Path(temp, 'org'); root.mkdir()
    config = Path(temp, 'config')
    header = '#+TODO: WAIT TODO PROG | DONE KILL\n'
    source = root / 'project.org'
    source.write_text(header + '* Context\n** TODO Planned task :initial:\nSCHEDULED: <2030-01-01 Tue>\nDescription\n** WAIT Awaiting reply\n')
    (root / 'other.org').write_text(header + '* Destination\n')
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
    env = dict(os.environ, XDG_CONFIG_HOME=str(config)); env.pop('ORG_API_TOKEN', None)
    process = subprocess.Popen([binary, 'serve', '-d', str(root), '--port', str(port)], env=env, stdout=log, stderr=log)
    base = f'http://127.0.0.1:{port}'
    try:
        for _ in range(200):
            try:
                urllib.request.urlopen(base, timeout=1).close(); break
            except OSError:
                assert process.poll() is None; time.sleep(.05)
        with sync_playwright() as pw:
            browser = pw.chromium.launch(executable_path=os.environ.get('CHROMIUM'), headless=True)
            page = browser.new_page(viewport={'width':1800,'height':1100})
            errors = []; page.on('pageerror', lambda e: errors.append(str(e)))
            page.goto(base)
            page.locator('#actor').fill('human')
            source.write_text(header + '#+CATEGORY: work\n* Notes\n:PROPERTIES:\n:ID: notes\n:CATEGORY: family\n:END:\n** TODO Rich [0/1]\n:PROPERTIES:\n:ID: rich\n:OWNER: alice\n:END:\n:LOGBOOK:\nCLOCK: [2026-01-01 Thu 09:00]--[2026-01-01 Thu 10:00] => 1:00\n- State "TODO" from "WAIT"\n:END:\n*bold* /italic/ =code= [[https://example.com][Example]] [[id:leaf][Leaf link]] [[javascript:alert(1)][Unsafe]]\n<script>window.injected=true</script>\n| Name | Count |\n|------+-------|\n| Test | 3 |\n- [ ] Group [0/2]\n  - [ ] One\n  - [ ] Two\n#+BEGIN_SRC js\nwindow.injected=true;\n- [ ] example only\n#+END_SRC\n*** TODO Branch\n:PROPERTIES:\n:ID: branch\n:END:\n**** DONE Leaf\n:PROPERTIES:\n:ID: leaf\n:END:\n*** DONE Finished child\n')
            expect(page.locator('#list .task').filter(has=page.locator('.task-title', has_text='Rich'))).to_have_count(1,timeout=10000)
            page.locator('#list .task').filter(has=page.locator('.task-title', has_text='Rich')).click()
            expect(page.locator('#entry-content strong')).to_have_text('bold')
            expect(page.locator('#entry-content em')).to_have_text('italic')
            expect(page.locator('#entry-content code')).to_have_text('code')
            expect(page.locator('#entry-content table tr')).to_have_count(2)
            expect(page.locator('#entry-content a')).to_have_attribute('href','https://example.com')
            assert not page.evaluate('window.injected === true')
            assert not page.locator('#entry-content a[href^="javascript:"]').count()
            expect(page.locator('#entry-content input[type=checkbox]')).to_have_count(3)
            page.locator('#entry-properties > summary').click()
            expect(page.locator('#entry-properties')).to_contain_text('Inherited · Notes')
            expect(page.locator('#entry-properties')).to_contain_text('Explicit · this heading')
            page.locator('#entry-time > summary').click()
            expect(page.locator('#entry-time')).to_contain_text('Completed clock time: 1:00')
            page.locator('#entry-time summary').filter(has_text='Work history').click()
            expect(page.locator('#entry-time pre')).to_contain_text('State "TODO"')
            page.locator('#entry-content input[type=checkbox]').nth(1).click()
            expect(page.locator('#message')).to_have_text('Checklist saved.')
            expect(page.locator('#entry-content input[type=checkbox]').first).to_have_js_property('indeterminate',True)
            assert '- [-] Group [1/2]' in source.read_text()
            page.locator('#entry-content input[type=checkbox]').first.click()
            expect(page.locator('#detail .detail-title')).to_have_text('Rich [1/1]')
            assert '- [X] Group [2/2]' in source.read_text()
            page.locator('#undo').click()
            expect(page.locator('#list .task').filter(has=page.locator('.task-title', has_text='Rich [0/1]'))).to_have_count(1)
            page.locator('#list .task').filter(has=page.locator('.task-title', has_text='Rich')).click()
            page.get_by_text('Subheadings · 2',exact=True).click()
            page.get_by_text('Expand subheadings',exact=True).click()
            expect(page.locator('.outline-link').filter(has_text='DONE Leaf')).to_be_visible()
            page.locator('.outline-link').filter(has_text='DONE Leaf').click()
            expect(page.locator('#detail .detail-title')).to_have_text('Leaf')
            page.locator('.breadcrumbs button').filter(has_text='Notes').click()
            expect(page.locator('#detail .detail-title')).to_have_text('Notes')
            page.wait_for_timeout(3500)
            expect(page.locator('#detail .detail-title')).to_have_text('Notes')
            page.get_by_text('Subheadings · 1',exact=True).click()
            page.locator('.outline-link').filter(has_text='Rich').click()
            page.locator('#entry-content').get_by_role('button',name='Source',exact=True).click()
            expect(page.locator('#entry-content > pre')).to_contain_text('*bold*')
            page.locator('#entry-content').get_by_role('button',name='Rendered',exact=True).click()
            # Race an external edit against a checked mutation: it must not overwrite the file.
            def conflict(route):
                source.write_text(source.read_text() + 'External addition\n')
                route.continue_()
            page.route('**/api/v1/entry_checkbox',conflict,times=1)
            page.locator('#entry-content input[type=checkbox]').nth(2).click()
            expect(page.locator('#message')).to_contain_text('File changed')
            assert 'External addition' in source.read_text() and '- [ ] Two' in source.read_text()
            page.set_viewport_size({'width':390,'height':844})
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            screenshot = os.environ.get('ORG_RICH_SCREENSHOT')
            if screenshot:
                page.set_viewport_size({'width':1400,'height':1100}); page.screenshot(path=screenshot,full_page=True)
            assert not errors,errors
            browser.close()
        print('PASS rich Org: safe rendering, checklists/cookies/undo/conflicts, hierarchy navigation, inherited properties, clocks/history, mobile')
    finally:
        process.send_signal(signal.SIGINT)
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
        if process.returncode:
            log.seek(0); print(log.read(), file=sys.stderr)
