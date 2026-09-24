import { expect, test, type Page } from '@playwright/test';

async function rect(page: Page, testId: string) {
  const box = await page.getByTestId(testId).boundingBox();
  expect(box, `${testId} is visible`).not.toBeNull();
  return box!;
}

test('outline clicks lead to entrances and a live path preview', async ({ page }) => {
  await page.goto('/');
  const map = page.locator('.map svg');
  const corners = [
    { x: 260, y: 360 }, { x: 1000, y: 360 },
    { x: 1000, y: 650 }, { x: 260, y: 650 },
  ];
  for (const corner of corners) await map.click({ position: corner });
  await map.click({ position: corners[0] });
  const footer = page.getByTestId('panel-footer');
  await expect(footer.locator('button').last()).toBeEnabled();
  await footer.locator('button').last().click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '1');
  await map.click({ position: { x: 610, y: 360 } });
  await expect(footer.locator('button').last()).toBeEnabled();
  await footer.locator('button').last().click();
  await expect.poll(() => page.locator('.map svg line').count()).toBeGreaterThan(0);
  await expect(page.getByText(/Live-Layout/)).toBeVisible();
});

test('new variant changes seed and redraws the live plaza', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const newVariant = page.getByRole('button', { name: /Neues Layout|Neue Variante/ });
  await newVariant.click();
  await expect(page.getByText('Live-Layout · Seed 2')).toBeVisible();
  await expect.poll(() => page.locator('.map svg line').count()).toBeGreaterThan(0);
  await newVariant.click();
  await expect(page.getByText('Live-Layout · Seed 3')).toBeVisible();
});

test('shipped panel CSS avoids unsupported CSS Grid', async ({ page }) => {
  const response = await page.request.get('/dist/mock.css');
  expect(response.ok()).toBeTruthy();
  const css = await response.text();
  expect(css).not.toMatch(/display:\s*grid\b/);
  expect(css).not.toMatch(/grid-template-/);
});

test('asset columns use stable flex layout with uniform gaps', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'decorated', exact: true }).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '2');
  const info = await rect(page, 'asset-header');
  const sliders = await rect(page, 'density-settings');
  const grid = await rect(page, 'asset-grid');
  const chooser = await rect(page, 'asset-chooser');
  const footer = await rect(page, 'panel-footer');
  const flowPositions = await page.evaluate(() => ['asset-header',
    'density-settings', 'asset-workspace'].map((id) =>
    getComputedStyle(document.querySelector(`[data-testid="${id}"]`)!).position));
  expect(flowPositions).toEqual(['static', 'static', 'static']);
  expect(sliders.x - (info.x + info.width)).toBeGreaterThanOrEqual(8);
  expect(chooser.x - (grid.x + grid.width)).toBeGreaterThanOrEqual(8);
  expect(grid.y - (info.y + info.height)).toBeGreaterThanOrEqual(8);
  expect(footer.y - (chooser.y + chooser.height)).toBeGreaterThanOrEqual(8);
  expect(Math.abs(grid.height - chooser.height)).toBeLessThan(2);
  expect(Math.abs(grid.width - chooser.width)).toBeLessThan(2);
  const tiles = await page.getByTestId('asset-grid').locator('[role="button"]').all();
  expect(tiles).toHaveLength(6);
  const tileBoxes = await Promise.all(tiles.map((tile) => tile.boundingBox()));
  expect(Math.abs(tileBoxes[0]!.y - tileBoxes[2]!.y)).toBeLessThan(2);
  expect(tileBoxes[3]!.y).toBeGreaterThan(tileBoxes[0]!.y + 50);
  const values = await page.locator('[role="slider"] + strong').all();
  for (const value of values) {
    const box = await value.boundingBox();
    expect(box!.width).toBeGreaterThanOrEqual(60);
    expect(box!.height).toBeLessThan(30);
    expect(await value.textContent()).toMatch(/^\d+%$/);
  }
  await page.screenshot({ path: 'test-results/asset-layout.png' });
});

test('mock toolbar remains below the panel at compact viewport', async ({ page }) => {
  await page.setViewportSize({ width: 1059, height: 800 });
  await page.goto('/');
  await page.getByRole('button', { name: 'decorated', exact: true }).click();
  const panel = await rect(page, 'park-panel');
  const toolbar = await page.locator('.mockbar').boundingBox();
  expect(toolbar).not.toBeNull();
  expect(toolbar!.y).toBeGreaterThanOrEqual(panel.y + panel.height);
  await page.screenshot({ path: 'test-results/compact-layout.png' });
});

test('all workflow panels preserve the same outer inset', async ({ page }) => {
  await page.goto('/');
  for (const preset of ['empty', 'paths', 'decorated']) {
    await page.getByRole('button', { name: preset, exact: true }).click();
    const positions = await page.evaluate(() => {
      const header = document.querySelector('[data-testid="panel-header"]')!;
      const body = document.querySelector('[data-testid="panel-body"]')!;
      const footer = document.querySelector('[data-testid="panel-footer"]')!;
      return [header, body, footer].map((element) =>
        element.firstElementChild!.getBoundingClientRect().left);
    });
    expect(Math.max(...positions) - Math.min(...positions)).toBeLessThan(2);
  }
});

test('path options remain inside panel at compact viewport', async ({ page }) => {
  await page.setViewportSize({ width: 1059, height: 800 });
  await page.goto('/');
  await page.getByRole('button', { name: 'paths', exact: true }).click();
  const body = await rect(page, 'panel-body');
  const settings = await rect(page, 'path-settings');
  expect(settings.x).toBeGreaterThanOrEqual(body.x);
  expect(settings.x + settings.width).toBeLessThanOrEqual(body.x + body.width);
  expect(settings.y + settings.height).toBeLessThanOrEqual(body.y + body.height);
});

test('plaza center choices wrap instead of clipping their last item', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const choices = page.getByTestId('plaza-center-choices');
  const first = await choices.locator('button').nth(1).boundingBox();
  const last = await choices.locator('button').last().boundingBox();
  expect(first).not.toBeNull();
  expect(last).not.toBeNull();
  expect(last!.y).toBeGreaterThan(first!.y);
  await choices.evaluate((element) => { element.scrollTop = element.scrollHeight; });
  const viewport = await choices.boundingBox();
  const visibleLast = await choices.locator('button').last().boundingBox();
  expect(visibleLast!.x + visibleLast!.width).toBeLessThanOrEqual(viewport!.x + viewport!.width);
  expect(visibleLast!.y + visibleLast!.height).toBeLessThanOrEqual(viewport!.y + viewport!.height + 1);
});

test('plaza detail panes have equal constrained height and one-line density', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await page.getByTestId('panel-footer').locator('button').last().click();
  await page.getByRole('button', { name: /Details/ }).first().click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '2');
  const slots = await rect(page, 'plaza-arrangement-slots');
  const picker = await rect(page, 'plaza-arrangement-picker');
  const footer = await rect(page, 'panel-footer');
  expect(Math.abs(slots.height - picker.height)).toBeLessThan(2);
  expect(Math.abs(slots.width - picker.width)).toBeLessThan(2);
  expect(footer.y - (picker.y + picker.height)).toBeGreaterThanOrEqual(8);
  const density = page.locator('[role="slider"] + strong');
  expect(await density.textContent()).toMatch(/^\d+%$/);
  expect((await density.boundingBox())!.height).toBeLessThan(30);
});
