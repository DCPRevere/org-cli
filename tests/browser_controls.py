"""Exercise visible disclosure controls rather than bypassing the UI."""
def show_list(page):
    back = page.get_by_role('button', name='← Back to tasks', exact=True)
    if back.is_visible():
        back.click()


def view(page, name):
    show_list(page)
    page.get_by_role('tab', name=name.capitalize(), exact=True).click()


def filters(page):
    show_list(page)
    if not page.locator('#filter-panel').is_visible():
        page.locator('#filters-toggle').click()


def select_filter(page, selector, value):
    filters(page)
    page.locator(selector).select_option(value)


def fill_filter(page, selector, value):
    filters(page)
    page.locator(selector).fill(value)


def action(page, name):
    button = page.get_by_role('button', name=name, exact=True)
    if not button.is_visible():
        page.locator('#task-actions > summary').click()
    page.get_by_role('button', name=name, exact=True).click()


def evidence(page, value):
    if not page.locator('#evidence').is_visible():
        action(page, 'Add evidence or feedback')
    page.locator('#evidence').fill(value)
