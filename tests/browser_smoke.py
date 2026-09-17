#!/usr/bin/env python3
"""Optional browser validation: pip install playwright; set CHROMIUM to its executable.
Uses only a disposable workspace. Usage: python tests/browser_smoke.py [org binary]
"""
import os
from datetime import date, timedelta
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
            page.emulate_media(color_scheme='dark')
            page.goto(base)
            expect(page.get_by_text('Shared work, clear ownership')).to_have_count(0)
            expect(page.locator('#theme')).to_have_value('system')
            expect(page.locator('body')).to_have_css('background-color', 'rgb(21, 27, 24)')
            page.locator('#theme').select_option('light')
            expect(page.locator('body')).to_have_css('background-color', 'rgb(246, 245, 241)')
            page.reload()
            expect(page.locator('#theme')).to_have_value('light')
            page.locator('#theme').select_option('dark')
            page.emulate_media(color_scheme='light')
            page.reload()
            expect(page.locator('#theme')).to_have_value('dark')
            expect(page.locator('body')).to_have_css('background-color', 'rgb(21, 27, 24)')
            page.locator('#theme').select_option('system')
            expect(page.locator('body')).to_have_css('background-color', 'rgb(246, 245, 241)')
            page.emulate_media(color_scheme='dark')
            expect(page.locator('body')).to_have_css('background-color', 'rgb(21, 27, 24)')
            expect(page.locator('#message')).to_contain_text('access token')
            expect(page.locator('#actor')).to_have_value('')
            expect(page.locator('#actor')).to_have_attribute('placeholder', 'Name for task history')
            page.locator('#actor').fill('worker')
            page.locator('#token').fill('browser-test-token')
            page.get_by_role('button', name='Unlock workspace', exact=True).click()
            expect(page.get_by_role('button', name='+ New task')).to_be_enabled()
            expect(page.locator('#connect')).to_be_hidden()
            expect(page.locator('#token')).to_be_hidden()
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
            expect(page.get_by_text('Requirements changed after submission.', exact=False)).to_be_visible(timeout=15000)
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
            expect(page.locator('#list .task')).to_have_count(0, timeout=15000)
            expect(page.get_by_text('This task is no longer in this view.', exact=False)).to_be_visible()
            # Automatic refresh must preserve drafts and their original revision.
            page.locator('#status').select_option('open')
            expect(page.locator('#list .task')).to_have_count(1)
            page.locator('#list .task').click()
            page.locator('#evidence').fill('Unsaved human evidence')
            page.get_by_text('Task settings', exact=True).click()
            page.locator('#edit-acceptance').fill('Unsaved acceptance draft')
            task_file.write_text(task_file.read_text().replace('direct edits are covered', 'external revision arrived'))
            expect(page.locator('#message')).to_contain_text('Your draft is preserved', timeout=15000)
            expect(page.locator('#evidence')).to_have_value('Unsaved human evidence')
            expect(page.locator('#edit-acceptance')).to_have_value('Unsaved acceptance draft')
            page.get_by_role('button', name='Claim task', exact=True).click()
            expect(page.locator('#message')).to_contain_text('File changed')
            assert ':TASK_CLAIM_ID:' not in task_file.read_text()
            # Explicit selection reloads the current version; deletion also arrives without Refresh.
            page.locator('#list .task').click()
            expect(page.locator('#edit-acceptance')).to_have_value('Tests pass; external revision arrived')
            task_file.write_text('')
            expect(page.locator('#list .task')).to_have_count(0, timeout=15000)
            expect(page.get_by_text('This task is no longer in this view.', exact=False)).to_be_visible()
            # Calendar/agenda data comes from real files, including every API page.
            today = date.today()
            yesterday = today - timedelta(days=1)
            source = Path(workspace, 'person', 'project.org')
            source.parent.mkdir()
            source.write_text(
                '* Launch context\n** TODO [#A] Calendar task :launch:\n'
                f'SCHEDULED: <{today} 09:30> DEADLINE: <{yesterday}>\n'
                ':PROPERTIES:\n:TASK_OWNER: daniel\n:TASK_ACCEPTANCE: Verify calendar cards\n:END:\n'
                + ''.join(f'** TODO Undated {i:03}\n' for i in range(105)))
            page.locator('#view').select_option('board')
            expect(page.locator('#list .task')).to_have_count(106, timeout=15000)
            expect(page.locator('#coverage')).to_have_text('All matching tasks loaded.')
            card = page.locator('#list .task').filter(has_text='Calendar task')
            expect(card).to_contain_text('File: person/project.org')
            expect(card).to_contain_text('Owner: daniel')
            expect(card).to_contain_text('Launch context')
            expect(card).to_contain_text('Overdue deadline')
            expect(card).to_contain_text('Priority A')
            card.click()
            expect(page.locator('#detail')).to_contain_text('Scheduled: ' + str(today) + ' 09:30')
            page.locator('#evidence').fill('Preserve across views')
            page.locator('#view').select_option('agenda')
            expect(page.locator('#list')).to_contain_text('Unscheduled · 105')
            expect(page.locator('#list')).to_contain_text('Today · ' + str(today))
            expect(page.locator('#list')).to_contain_text('Overdue / scheduled earlier')
            expect(page.locator('#evidence')).to_have_value('Preserve across views')
            page.locator('#view').select_option('calendar')
            expect(page.locator('.calendar-day[data-date="' + str(today) + '"]')).to_contain_text('Scheduled:')
            yesterday_cell = page.locator('.calendar-day[data-date="' + str(yesterday) + '"]')
            if yesterday_cell.count():
                expect(yesterday_cell).to_contain_text('Deadline:')
            page.locator('.calendar-day[data-date="' + str(today) + '"] .task').click()
            expect(page.locator('#detail .detail-title')).to_have_text('Calendar task')
            expect(page.locator('#detail')).to_contain_text('Owner: daniel')
            expect(page.locator('#list')).to_contain_text('Unscheduled · 105')
            page.locator('#calendar-scale').select_option('week')
            expect(page.locator('.calendar-day')).to_have_count(7)
            page.get_by_role('button', name='Next', exact=True).click()
            page.get_by_role('button', name='Today', exact=True).click()
            expect(page.locator('.calendar-day.today')).to_have_count(1)
            page.get_by_text('Unscheduled · 105', exact=True).click()
            expect(page.locator('details.agenda-group')).to_have_attribute('open', '')
            page.get_by_role('button', name='Refresh', exact=True).click()
            expect(page.locator('body')).to_have_attribute('aria-busy', 'false')
            expect(page.locator('details.agenda-group')).to_have_attribute('open', '')
            page.get_by_text('Unscheduled · 105', exact=True).click()
            page.locator('#owner-filter').fill('daniel')
            page.locator('#owner-filter').press('Tab')
            expect(page.locator('#count')).to_have_text('· 1')
            expect(page.locator('#list')).not_to_contain_text('Undated')
            page.locator('#owner-filter').fill('')
            page.locator('#owner-filter').press('Tab')
            expect(page.locator('#count')).to_have_text('· 106')
            page.locator('#search-filter').fill('Undated 104')
            page.locator('#search-filter').press('Tab')
            expect(page.locator('#list .task')).to_have_count(1)
            expect(page.locator('#list .task')).to_contain_text('Undated 104')
            page.locator('#search-filter').fill('')
            page.locator('#search-filter').press('Tab')
            expect(page.locator('#list')).to_contain_text('Unscheduled · 105')
            for layout in ['list', 'board', 'agenda', 'calendar']:
                page.locator('#view').select_option(layout)
                expect(page.locator('body')).to_have_attribute('aria-busy', 'false')
                page.set_viewport_size({'width': 390, 'height': 844})
                if page.evaluate('document.documentElement.scrollWidth > window.innerWidth'):
                    page.screenshot(path='/tmp/org-views-overflow.png', full_page=True)
                    print(page.evaluate("[...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > innerWidth).map(e => [e.tagName, e.id, e.className, e.getBoundingClientRect().right]).slice(0, 20)"))
                    raise AssertionError(layout + ' overflow')
            page.set_viewport_size({'width': 1400, 'height': 1000})
            page.locator('#evidence').fill('')
            page.get_by_role('button', name='Cancel task', exact=True).click()
            expect(page.locator('#detail > .badge')).to_have_text('cancelled')
            assert 'Task cancel by reviewer' in source.read_text()
            page.reload()
            expect(page.locator('#actor')).to_have_value('reviewer')
            expect(page.locator('#token')).to_have_value('')
            expect(page.get_by_role('button', name='Unlock workspace')).to_be_visible()
            screenshot = os.environ.get('ORG_BOARD_SCREENSHOT')
            if screenshot:
                page.screenshot(path=screenshot, full_page=False)
            page.set_viewport_size({'width': 390, 'height': 844})
            assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
            assert not errors, errors
            # The usual local server needs neither a token nor a Connect action.
            with tempfile.TemporaryDirectory(prefix='org-board-local-') as local_workspace:
                with socket.socket() as probe:
                    probe.bind(('127.0.0.1', 0))
                    local_port = probe.getsockname()[1]
                Path(local_workspace, 'example.org').write_text('* TODO Open task\n* DONE Finished task\n')
                local_env = dict(os.environ)
                local_env.pop('ORG_API_TOKEN', None)
                local_process = subprocess.Popen(
                    [binary, 'serve', '-d', local_workspace, '--port', str(local_port)],
                    env=local_env, stdout=log, stderr=log)
                try:
                    local_url = f'http://127.0.0.1:{local_port}'
                    for _ in range(200):
                        try:
                            urllib.request.urlopen(local_url, timeout=1).close()
                            break
                        except OSError:
                            assert local_process.poll() is None
                            time.sleep(.05)
                    local_page = browser.new_page()
                    local_page.goto(local_url)
                    expect(local_page.get_by_role('button', name='+ New task')).to_be_enabled()
                    expect(local_page.locator('#coverage')).to_have_text('All matching tasks loaded.')
                    expect(local_page.locator('#connect')).to_be_hidden()
                    expect(local_page.locator('#token')).to_be_hidden()
                    expect(local_page.locator('#actor')).to_have_value('')
                    expect(local_page.locator('#list .task')).to_have_count(1)
                    # A filter change during an in-flight refresh must not be dropped.
                    held = []
                    local_page.route('**/api/v1/tasks', lambda route: held.append(route), times=1)
                    local_page.get_by_role('button', name='Refresh', exact=True).click()
                    expect(local_page.locator('#activity')).to_contain_text('Loading')
                    expect(local_page.locator('#new')).to_be_disabled()
                    local_page.locator('#status').select_option('done')
                    assert held
                    held[0].continue_()
                    expect(local_page.locator('#list .task')).to_contain_text('Finished task')
                    expect(local_page.locator('#activity')).to_be_empty()
                    # Transport failures must produce an actionable message and a usable retry.
                    local_page.route('**/api/v1/tasks', lambda route: route.abort(), times=1)
                    local_page.get_by_role('button', name='Refresh', exact=True).click()
                    expect(local_page.locator('#message')).to_contain_text('Check that the service is running')
                    expect(local_page.locator('body')).to_have_attribute('aria-busy', 'false')
                    local_page.get_by_role('button', name='Refresh', exact=True).click()
                    expect(local_page.locator('#message')).to_be_empty()
                    expect(local_page.locator('#list .task')).to_contain_text('Finished task')
                    # Opening and dismissing creation must not write a file.
                    local_page.get_by_role('button', name='+ New task').click()
                    expect(local_page.locator('#create-dialog')).to_be_visible()
                    local_page.locator('#create-cancel').click()
                    expect(local_page.locator('#create-dialog')).to_be_hidden()
                    assert not Path(local_workspace, 'tasks.org').exists()
                    local_page.close()
                finally:
                    local_process.send_signal(signal.SIGINT)
                    try:
                        local_process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        local_process.kill()
                        local_process.wait()
            browser.close()
        assert Path(workspace, 'tasks.org').read_text() == ''
        print('PASS browser: authentication, create, claim, submit, separate review, direct-edit invalidation and recovery, manual state changes, literal content, multi-page board/agenda/calendar, shared filters, mobile layout')
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
