#!/usr/bin/env python3
"""Browser design and interaction checks using disposable Org files and configuration."""
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
from browser_controls import action

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
            page.locator('#actor').fill('daniel')
            source.write_text(header + '* Release\n** TODO Prepare release notes [1/2]\nSCHEDULED: <2030-01-05 Sat> DEADLINE: <2030-01-08 Tue>\n:PROPERTIES:\n:ID: release\n:TASK_OWNER: daniel\n:END:\nWrite a clear summary of the changes for people upgrading.\n\n*Audience*: existing users and package maintainers.\n- [X] Collect the changes\n- [ ] Write the upgrade instructions\n*** TODO Check package names\n** WAIT Review installation guide\n:PROPERTIES:\n:ID: guide\n:TASK_OWNER: alice\n:END:\nWaiting for the packaging review.\n')
            card = page.locator('#list .task').filter(has=page.locator('.task-title',has_text='Prepare release notes'))
            expect(card).to_have_count(1,timeout=10000)
            expect(page.locator('#filter-panel')).to_be_hidden()
            assert card.bounding_box()['y'] < 420
            expect(card).not_to_contain_text('*Audience*')
            page.locator('#filters-toggle').click()
            page.locator('#owner-filter').fill('daniel'); page.locator('#owner-filter').press('Tab')
            expect(page.locator('#active-filters')).to_contain_text('Owner: daniel')
            page.get_by_role('button',name='Remove owner filter',exact=True).click()
            expect(page.locator('#owner-filter')).to_have_value('')
            page.keyboard.press('Escape')
            expect(page.locator('#filter-panel')).to_be_hidden()
            expect(page.locator('#filters-toggle')).to_be_focused()
            page.get_by_role('tab',name='List',exact=True).focus(); page.keyboard.press('ArrowRight')
            expect(page.get_by_role('tab',name='Board',exact=True)).to_have_attribute('aria-selected','true')
            page.keyboard.press('Home')
            expect(page.get_by_role('tab',name='List',exact=True)).to_have_attribute('aria-selected','true')
            card.click()
            expect(page.locator('#evidence-panel')).to_be_hidden()
            expect(page.get_by_role('button',name='Cancel task',exact=True)).to_have_count(0)
            expect(page.locator('#detail')).not_to_contain_text('No acceptance criteria recorded')
            assert page.locator('#entry-content').bounding_box()['y'] < 600
            page.set_viewport_size({'width':1400,'height':1000})
            page.screenshot(animations='disabled', path='/tmp/org-design-light.png')
            page.locator('#theme').select_option('dark')
            page.screenshot(animations='disabled', path='/tmp/org-design-dark.png')
            page.locator('#theme').select_option('light')
            aside_top = page.locator('aside').evaluate('node => node.scrollTop')
            page.locator('#detail').evaluate('node => node.scrollTop = 500')
            assert page.locator('aside').evaluate('node => node.scrollTop') == aside_top
            assert page.evaluate('window.scrollY') == 0
            assert page.locator('.detail-header').bounding_box()['y'] >= 0
            page.locator('#detail').evaluate('node => node.scrollTop = 0')
            page.set_viewport_size({'width':390,'height':844})
            expect(page.locator('aside')).to_be_hidden()
            expect(page.get_by_role('button',name='← Back to tasks',exact=True)).to_be_visible()
            page.screenshot(animations='disabled', path='/tmp/org-design-mobile.png')
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            page.get_by_text('Task settings',exact=True).click(); page.locator('#edit-title').fill('Draft stays here')
            page.get_by_role('button',name='← Back to tasks',exact=True).click()
            expect(page.locator('aside')).to_be_visible()
            card.click()
            expect(page.locator('#edit-title')).to_have_value('Draft stays here')
            action(page,'Reload task (discard draft)')
            expect(page.locator('#edit-title')).to_have_value('Prepare release notes [1/2]')
            page.get_by_role('button',name='← Back to tasks',exact=True).click()
            source.write_text(source.read_text().replace('Write a clear summary','Write a concise summary'))
            page.wait_for_timeout(3500)
            expect(page.locator('aside')).to_be_visible()
            page.set_viewport_size({'width':1400,'height':1000})
            card.click()
            page.get_by_role('button',name='Claim task',exact=True).click()
            expect(page.locator('#detail .coordination').first).to_have_text('working')
            expect(page.locator('#evidence-panel')).to_be_hidden()
            page.get_by_role('button',name='Submit work',exact=True).click()
            expect(page.locator('#evidence-panel')).to_be_visible()
            expect(page.locator('#evidence')).to_be_focused()
            page.locator('#evidence').fill('Release notes reviewed against the changes.')
            page.get_by_role('button',name='Confirm submit work',exact=True).click()
            expect(page.locator('#detail .coordination').first).to_have_text('review')
            expect(page.locator('#message')).to_have_text('Submitted for review.')
            expect(page.locator('#message')).to_have_attribute('data-kind','success')
            # Routine polling should be silent and leave focus alone.
            page.wait_for_timeout(3500)
            expect(page.locator('#activity')).to_be_empty()
            assert not errors,errors
            browser.close()
        print('PASS design: compact filters/chips, keyboard tabs, content hierarchy, light/dark/mobile, independent scrolling, Back/draft preservation, contextual evidence and quiet polling')
    finally:
        process.send_signal(signal.SIGINT)
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
        if process.returncode:
            log.seek(0); print(log.read(), file=sys.stderr)
