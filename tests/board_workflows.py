#!/usr/bin/env python3
"""Real browser checks for file-faithful workflows; all files/config are disposable."""
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
from browser_controls import view, select_filter, action

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
            expect(page.locator('#list .task')).to_have_count(2)
            page.locator('#actor').fill('human')
            view(page, 'board')
            expect(page.locator('.column')).to_have_count(5)
            assert page.locator('.column').evaluate_all('(nodes) => nodes.map(n => n.dataset.state)') == ['WAIT','TODO','PROG','DONE','KILL']
            planned = page.locator('.column[data-state="TODO"] .task')
            expect(planned).to_contain_text('ready')
            planned.drag_to(page.locator('.column[data-state="PROG"] h3'))
            expect(page.locator('#detail .detail-header > .org-state')).to_have_text('PROG')
            assert '** PROG Planned task' in source.read_text()
            page.locator('#undo').click()
            expect(page.locator('.column[data-state="TODO"] .task')).to_have_count(1)
            assert '** TODO Planned task' in source.read_text()
            # Shared order is durable outside notes and observable in a separate browser context.
            page.get_by_role('button', name='Move TODO left', exact=True).click()
            expect(page.locator('#message')).to_have_text('Column order saved.')
            assert page.locator('.column').first.get_attribute('data-state') == 'TODO'
            settings = list(config.glob('org-cli/workspaces/*/board.json'))
            assert len(settings) == 1 and 'column_order' in settings[0].read_text()
            assert not list(root.glob('**/*.json'))
            second = browser.new_page(viewport={'width':1800,'height':1100})
            second.goto(base); expect(second.locator('#list .task')).to_have_count(2)
            view(second, 'board')
            expect(second.locator('.column').first).to_have_attribute('data-state', 'TODO')
            page.get_by_role('button', name='Move PROG left', exact=True).click()
            expect(second.locator('.column').nth(1)).to_have_attribute('data-state', 'PROG', timeout=15000)
            second.close()
            page.locator('.column[data-state="TODO"] .task').click()
            expect(page.locator('#detail .detail-title')).to_have_text('Planned task')
            page.get_by_text('Task settings', exact=True).click()
            page.locator('#edit-title').fill('Edited task')
            page.locator('#edit-text').fill('Description saved')
            page.locator('#edit-tags').fill('urgent team')
            page.locator('#edit-owner').fill('alice')
            page.locator('#edit-priority').fill('A')
            page.locator('#edit-deadline').fill('2030-01-02T17:00')
            page.get_by_role('button', name='Save settings', exact=True).click()
            expect(page.locator('#detail .detail-title')).to_have_text('Edited task')
            expect(page.locator('#detail')).to_contain_text('Owner: alice')
            assert 'DEADLINE: <2030-01-02' in source.read_text() and '17:00' in source.read_text()
            assert '** WAIT Awaiting reply' in source.read_text()
            page.get_by_text('Move to another file or heading', exact=True).click()
            expect(page.locator('body')).to_have_attribute('aria-busy','false')
            page.locator('#move-file').fill('other.org'); page.locator('#move-file').press('Tab')
            page.locator('#move-parent').select_option(label='Destination')
            page.get_by_role('button', name='Move task', exact=True).click()
            expect(page.locator('#message')).to_have_text('Task moved.')
            assert 'Edited task' not in source.read_text()
            assert '** TODO [#A] Edited task' in (root / 'other.org').read_text(), (root / 'other.org').read_text()
            page.locator('#undo').click()
            expect(page.locator('#message')).to_have_text('Last change undone.')
            assert 'Edited task' in source.read_text() and 'Edited task' not in (root / 'other.org').read_text()
            # Creation uses the chosen file and parent instead of a special tasks.org.
            page.get_by_role('button', name='+ New task').click()
            page.locator('#create-file').fill('other.org'); page.locator('#create-file').press('Tab')
            page.locator('#create-parent').select_option(label='Destination')
            page.locator('#create-state').select_option('WAIT')
            page.locator('#create-title').fill('New child')
            page.get_by_role('button', name='Create task', exact=True).click()
            expect(page.locator('#detail .detail-header > .org-state')).to_have_text('WAIT')
            assert '** WAIT New child' in (root / 'other.org').read_text()
            assert not (root / 'tasks.org').exists()
            # Explicit conflict comparison leaves the draft intact, and reload is explicit.
            page.locator('.task').filter(has_text='Edited task').click()
            expect(page.locator('#detail .detail-title')).to_have_text('Edited task')
            page.get_by_text('Task settings', exact=True).click()
            page.locator('#edit-text').fill('Unsaved draft')
            source.write_text(source.read_text().replace('Description saved','Externally changed description'))
            expect(page.locator('#message')).to_contain_text('Your draft is preserved', timeout=15000)
            action(page, 'Compare with file')
            expect(page.locator('#detail')).to_contain_text('Externally changed description')
            expect(page.locator('#edit-text')).to_have_value('Unsaved draft')
            page.get_by_role('button', name='Save settings', exact=True).click()
            expect(page.locator('#message')).to_contain_text('File changed')
            expect(page.locator('#edit-text')).to_have_value('Unsaved draft')
            action(page, 'Reload task (discard draft)')
            expect(page.locator('#edit-text')).to_have_value('Externally changed description')
            # Save/restore a named view and the current view across reloads.
            select_filter(page, '#file-filter', 'project.org')
            expect(page.locator('#list .task')).to_have_count(2)
            page.once('dialog', lambda dialog: dialog.accept('Project work'))
            page.locator('#save-view').click()
            view(page, 'agenda')
            expect(page.locator('body')).to_have_attribute('aria-busy','false')
            page.reload()
            expect(page.locator('#view')).to_have_value('agenda')
            expect(page.locator('#file-filter')).to_have_value('project.org')
            select_filter(page, '#saved-view', 'Project work')
            expect(page.locator('#view')).to_have_value('board')
            page.set_viewport_size({'width':390,'height':844})
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            assert not errors, errors
            screenshot = os.environ.get('ORG_WORKFLOW_SCREENSHOT')
            if screenshot:
                page.set_viewport_size({'width':1800,'height':1100})
                page.screenshot(path=screenshot)
            browser.close()
        print('PASS file workflows: exact states, planned readiness, card moves, column persistence, fields, destination/parent, checked undo, conflict drafts, saved views, mobile')
    finally:
        process.send_signal(signal.SIGINT)
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
        if process.returncode:
            log.seek(0); print(log.read(), file=sys.stderr)
