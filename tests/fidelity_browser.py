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
            page.clock.set_fixed_time(datetime(2030, 1, 3, 15, tzinfo=timezone.utc))
            page.goto(base)
            page.locator('#actor').fill('human')
            # Whole own-heading content and inherited tags are searchable.
            source.write_text(header + '#+FILETAGS: :office:\n* Context :team:\n** TODO Body search :local:\nDEADLINE: <2030-01-03 Thu 09:00>\nOnlyBodyNeedle\n** TODO Repeat\nSCHEDULED: <2030-01-01 Tue +1w>--<2030-01-02 Wed>\n* Meeting\n<2030-01-03 Thu 09:00-10:00>\n* Relative repeat\n<2030-01-04 Fri .+1w>\n')
            page.locator('#search-filter').fill('OnlyBodyNeedle'); page.locator('#search-filter').press('Tab')
            expect(page.locator('#list .task')).to_have_count(1, timeout=10000)
            expect(page.locator('#list')).to_contain_text('Inherited tags: office, team')
            expect(page.locator('#list')).to_contain_text('Overdue deadline')
            page.locator('#search-filter').fill('office'); page.locator('#search-filter').press('Tab')
            expect(page.locator('#list .task')).to_have_count(2)
            page.locator('#search-filter').fill(''); page.locator('#search-filter').press('Tab')
            # Mutation response reports the resulting state, not requested DONE.
            page.locator('#list .task').filter(has_text='Repeat').click()
            page.locator('#edit-state').select_option('DONE'); page.get_by_role('button', name='Change state', exact=True).click()
            expect(page.locator('#message')).to_contain_text('Occurrence completed; file state is TODO')
            assert '<2030-01-08' in source.read_text() and '--<2030-01-09' in source.read_text()
            page.locator('#edit-state').select_option('KILL'); page.get_by_role('button', name='Change state', exact=True).click()
            expect(page.locator('#detail > .org-state')).to_have_text('KILL')
            assert '<2030-01-08' in source.read_text() and '<2030-01-15' not in source.read_text()
            # Date-time compares the exact local time, while date-only is due through that day.
            assert page.evaluate("overdue({date:'2030-01-01',time:'09:00'},new Date('2030-01-01T15:00'))")
            assert not page.evaluate("overdue({date:'2030-01-01'},new Date('2030-01-01T15:00'))")
            assert not page.evaluate("overdue({date:'2030-01-01',time:'17:00'},new Date('2030-01-01T15:00'))")
            page.locator('#view').select_option('calendar')
            page.evaluate("calendarDate = new Date('2030-01-01T12:00'); renderList()")
            expect(page.locator('.calendar-day[data-date="2030-01-03"]')).to_contain_text('Meeting')
            expect(page.locator('.calendar-day[data-date="2030-01-03"]')).to_contain_text('09:00 – 2030-01-03 10:00')
            page.locator('.calendar-day[data-date="2030-01-03"] .task').filter(has_text='Meeting').click()
            expect(page.locator('#detail')).to_contain_text('Appointment · Source Org')
            expect(page.locator('#edit-state')).to_have_count(0)
            assert not page.locator('.calendar-day[data-date="2030-01-11"]').get_by_text('Relative repeat', exact=True).count()
            # Live file/workflow catalogue refresh preserves an in-progress editor draft.
            page.locator('#view').select_option('list')
            page.locator('#list .task').filter(has_text='Body search').click()
            page.get_by_text('Task settings', exact=True).click()
            page.locator('#edit-title').fill('Unsaved title')
            (root / 'fresh.org').write_text('#+TODO: OPEN | CLOSED\n* OPEN Fresh\n')
            expect(page.locator('#file-filter option[value="fresh.org"]')).to_have_count(1, timeout=10000)
            expect(page.locator('#state-filter option[value="OPEN"]')).to_have_count(1)
            expect(page.locator('#edit-title')).to_have_value('Unsaved title')
            # Full timestamp editing includes repeaters, warning/delay and endpoints.
            page.locator('#edit-scheduled').fill('<2030-01-01 09:00 +1w --2d>--<2030-01-02 10:00>')
            page.get_by_role('button', name='Save settings', exact=True).click()
            expect(page.locator('#message')).to_have_text('Task saved.')
            assert '+1w --2d>' in source.read_text()
            page.locator('#view').select_option('calendar')
            expect(page.locator('.calendar-day[data-date="2030-01-01"]')).to_contain_text('Unsaved title')
            expect(page.locator('.calendar-day[data-date="2030-01-02"]')).to_contain_text('Unsaved title')
            expect(page.locator('.calendar-day[data-date="2030-01-08"]')).to_contain_text('Projected Scheduled')
            expect(page.locator('.calendar-day[data-date="2030-01-09"]')).to_contain_text('Unsaved title')
            page.locator('#view').select_option('agenda')
            expect(page.locator('#list')).to_contain_text('Meeting')
            expect(page.locator('.agenda-group').filter(has=page.get_by_role('heading', name='2030-01-09', exact=True))).to_contain_text('Unsaved title')
            # Long-ago hourly repeats skip to the visible window, and month ends clamp.
            preview = page.evaluate("planningEvents([{title:'Old',status:'ready',scheduled:{date:'1990-01-01',time:'09:00',repeater:'+1h'}}], '2030-01-01', '2030-01-02')")
            assert len(preview) == 48, len(preview)
            month = page.evaluate("planningEvents([{title:'Monthly',status:'ready',scheduled:{date:'2030-01-31',repeater:'+1m'}}], '2030-02-01', '2030-02-28')")
            assert month[0]['stamp']['date'] == '2030-02-28'
            assert not errors, errors
            browser.close()
        print('PASS Org fidelity: recurrence completion/cancellation, timed deadlines, appointments, projected ranges, full timestamp editing, inherited tags/body search, live catalogue and draft preservation')
    finally:
        process.send_signal(signal.SIGINT)
        try: process.wait(timeout=15)
        except subprocess.TimeoutExpired: process.kill(); process.wait()
        if process.returncode:
            log.seek(0); print(log.read(), file=sys.stderr)
