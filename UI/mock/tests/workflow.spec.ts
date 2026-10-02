import { expect, test, type Page } from '@playwright/test';

async function rect(page: Page, testId: string) {
  const box = await page.getByTestId(testId).boundingBox();
  expect(box, `${testId} is visible`).not.toBeNull();
  return box!;
}

const corners = [
  { x: 260, y: 360 }, { x: 1000, y: 360 },
  { x: 1000, y: 650 }, { x: 260, y: 650 },
];

async function drawOutline(page: Page) {
  const map = page.locator('.map svg');
  for (const corner of corners) await map.click({ position: corner });
  await map.click({ position: corners[0] });
  return map;
}

const mainAction = (page: Page) => page.getByTestId('main-action');

test('the main button walks a park from outline to finished build', async ({ page }) => {
  await page.goto('/');
  await expect.poll(() => page.getByTestId('brand-logo').evaluate((image) =>
    (image as HTMLImageElement).complete
      && (image as HTMLImageElement).naturalWidth > 0)).toBe(true);
  const body = page.getByTestId('panel-body');
  await expect(mainAction(page)).toHaveAttribute('data-action', 'drawOutline');
  await expect(mainAction(page)).toBeDisabled();
  await expect(page.getByTestId('snap-controls')).toBeVisible();

  const map = await drawOutline(page);
  await expect(mainAction(page)).toHaveAttribute('data-action', 'placeEntrances');
  await expect(page.getByTestId('outline-state')).toHaveText('4 Punkte · bereit für die Planung');
  await mainAction(page).click();
  const mode = page.getByTestId('mode-selector');
  await expect(mode.getByRole('button', { name: 'Eingänge', exact: true }))
    .toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('snap-controls')).toHaveCount(0);
  await expect(mainAction(page)).toHaveAttribute('data-action', 'markEntrance');
  await expect(mainAction(page)).toBeDisabled();

  await map.click({ position: { x: 610, y: 360 } });
  await expect(page.getByTestId('action-column')).toContainText('1 Eingang markiert');
  await expect(mainAction(page)).toHaveText('Park planen');
  await mainAction(page).click();
  await expect.poll(() => page.locator('.map svg line').count()).toBeGreaterThan(0);
  await expect(page.getByText(/Live-Layout/)).toBeVisible();
  await expect(page.getByTestId('paths-variant')).toBeEnabled();

  // Without a ground surface the main button only says what is missing.
  await expect(mainAction(page)).toHaveAttribute('data-action', 'chooseSurface');
  await expect(mainAction(page)).toBeDisabled();
  await page.getByTestId('park-surface-choices')
    .getByRole('button', { name: 'Gras 1', exact: true }).click();
  // A changed asset choice discards the furnishing preview; plan it again.
  await expect(mainAction(page)).toHaveText('Ausstattung planen');
  await mainAction(page).click();
  await expect(mainAction(page)).toHaveText('Park bauen');
  await mainAction(page).click();
  await expect(body).toHaveAttribute('data-decorations-built', 'true');
  await expect(body).toHaveAttribute('data-paths-built', 'true');
  await expect(page.getByTestId('park-surface-choices').locator('button').first())
    .toBeDisabled();
  await expect(page.getByTestId('remove-built')).toBeVisible();
  await expect(mainAction(page)).toHaveText('Park fertigstellen');
  await mainAction(page).click();
  await expect(body).toHaveAttribute('data-paths-built', 'false');
  await expect(page.getByTestId('outline-state')).toHaveText('Noch keine Punkte gesetzt');
});

test('the outline can be edited again while entrances are set', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'paths', exact: true }).click();
  const mode = page.getByTestId('mode-selector');
  const outline = mode.getByRole('button', { name: 'Umriss', exact: true });
  await outline.click();
  await expect(outline).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('snap-controls')).toBeVisible();
  await mode.getByRole('button', { name: 'Eingänge', exact: true }).click();
  await expect(page.getByTestId('snap-controls')).toHaveCount(0);
});

for (const plaza of [false, true]) {
  test(`a rejected ${plaza ? 'plaza' : 'park'} build keeps both previews`, async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: plaza ? 'plaza' : 'decorated', exact: true }).click();
    const body = page.getByTestId('panel-body');
    await expect(mainAction(page)).toHaveAttribute('data-action', 'build');
    await page.getByRole('button', { name: 'Fehlerstatus testen' }).click();
    await mainAction(page).click();
    await expect(mainAction(page)).toHaveAttribute('data-action', 'busy');
    await expect(page.getByTestId('reset-outline')).toBeDisabled();
    await expect(mainAction(page)).toHaveAttribute('data-action', 'build');
    await expect(body).toHaveAttribute('data-paths-built', 'false');
    await expect(body).toHaveAttribute('data-decorations-built', 'false');
    await expect(page.getByTestId('paths-variant')).toBeEnabled();
  });
}

test('path notices use explicit status in the header', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await page.getByRole('button', { name: 'Fehlerstatus testen' }).click();
  const notice = page.getByRole('alert');
  await expect(notice).toHaveAttribute('data-status', 'error');
  await expect(notice).toHaveText('Validation failed: selected area is blocked.');
  const header = await rect(page, 'panel-header');
  const box = (await notice.boundingBox())!;
  expect(box.y + box.height).toBeLessThanOrEqual(header.y + header.height);
});

test('message keys from the game are translated for the active language', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await page.getByRole('button', { name: 'Meldungsschlüssel testen' }).click();
  const notice = page.getByRole('alert');
  await expect(notice).toHaveAttribute('data-status', 'warning');
  await expect(notice).toHaveText(
    'Hinweis: Weg stark geneigt bei X 12, Z 0. Bau wird trotzdem versucht.');
  await page.getByRole('button', { name: 'Sprache wechseln' }).click();
  await expect(notice).toHaveText('Note: steep path at X 12, Z 0. Building anyway.');
  await expect(mainAction(page)).toHaveText('Build plaza');
  await page.getByRole('button', { name: 'Sprache wechseln' }).click();
  await page.getByRole('button', { name: 'Fehlerstatus testen' }).click();
  // Plain text (older builds, mock values) is still shown verbatim.
  await expect(notice).toHaveText('Validation failed: selected area is blocked.');
});

test('a plaza can be drawn, configured, built and finished', async ({ page }) => {
  await page.goto('/');
  const map = await drawOutline(page);
  await mainAction(page).click();
  await map.click({ position: { x: 610, y: 360 } });
  const actions = page.getByTestId('action-column');
  await expect(actions).toContainText('1 Eingang markiert');
  await page.getByTestId('site-type-selector')
    .getByRole('button', { name: 'Plaza', exact: true }).click();
  await expect(actions).toContainText('1 Zugang markiert');
  await expect(page.getByTestId('mode-selector')
    .getByRole('button', { name: 'Zugänge', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Mock Fountain', exact: true }).click();
  const fence = page.getByTestId('plaza-fence-choices')
    .getByRole('button', { name: 'Zaun 1', exact: true });
  await fence.click();
  await expect(fence).toHaveAttribute('aria-pressed', 'true');
  await page.getByTestId('plaza-surface-choices')
    .getByRole('button', { name: 'Gras 1', exact: true }).click();

  await expect(mainAction(page)).toHaveText('Plaza planen');
  await mainAction(page).click();
  await expect(page.getByTestId('live-plan-summary')).toContainText('Plaza-Regeln');
  await expect.poll(() => page.getByTestId('live-centerpiece').count()).toBeGreaterThan(0);
  await expect.poll(() => page.getByTestId('live-furniture').count()).toBeGreaterThan(0);
  await expect.poll(() => page.getByTestId('live-plaza-fence').count()).toBeGreaterThan(0);

  await page.getByTestId('plaza-arrangement-edit').click();
  await expect(page.getByTestId('asset-window')).toBeVisible();
  const slots = page.getByTestId('plaza-arrangement-slots');
  await slots.getByRole('button', { name: 'Element hinzufügen' }).click();
  const picker = page.getByTestId('plaza-arrangement-picker');
  await picker.getByRole('button', { name: 'Laternen', exact: true }).click();
  await picker.getByRole('button', { name: 'Laterne 1', exact: true }).click();
  await expect(slots.getByRole('button').nth(1)).toContainText('Laterne 1');
  await page.getByTestId('plaza-arrangement-edit').click();
  await expect(page.getByTestId('asset-window')).toHaveCount(0);

  await expect(mainAction(page)).toHaveText('Plaza bauen');
  await mainAction(page).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-decorations-built', 'true');
  await expect(page.getByTestId('remove-built')).toHaveText('Plaza entfernen');
  await expect(page.getByRole('button', { name: 'Mock Fountain', exact: true })).toBeDisabled();
  await mainAction(page).click();
  await expect(page.getByTestId('outline-state')).toHaveText('Noch keine Punkte gesetzt');
});

test('a new plaza variant rolls reproducible settings into the controls', async ({ page }) => {
  const rollOnce = async () => {
    await page.goto('/');
    await page.getByRole('button', { name: 'plaza', exact: true }).click();
    const summary = page.getByTestId('live-plan-summary');
    await expect(summary).toContainText('Plaza-Regeln · Seed 1');
    await page.getByTestId('paths-variant').click();
    await expect(summary).toContainText('Seed 2');
    expect(await page.locator('.map svg line').count()).toBe(0);
    const pressed = await page.getByTestId('panel-body')
      .locator('[aria-pressed="true"]').evaluateAll((items) =>
        items.map((item) => item.getAttribute('aria-label') || item.textContent));
    const sliders = await page.getByRole('slider').evaluateAll((items) =>
      items.map((item) => item.getAttribute('aria-valuenow')));
    return JSON.stringify({ pressed, sliders });
  };
  const first = await rollOnce();
  expect(await rollOnce()).toBe(first);
  await page.getByTestId('paths-variant').click();
  await expect(page.getByTestId('live-plan-summary')).toContainText('Seed 3');
});

test('shared sliders support arrow, home, end, and page keys', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const slider = page.getByRole('slider', { name: 'Zur Mitte' });
  await expect(slider).toHaveAttribute('aria-valuenow', '4');
  await slider.focus();

  await page.keyboard.press('ArrowRight');
  await expect(slider).toHaveAttribute('aria-valuenow', '5');
  await page.keyboard.press('Home');
  await expect(slider).toHaveAttribute('aria-valuenow', '0');
  await page.keyboard.press('End');
  await expect(slider).toHaveAttribute('aria-valuenow', '20');
  await page.keyboard.press('PageDown');
  await expect(slider).toHaveAttribute('aria-valuenow', '10');
});

test('shared sliders react to game-compatible mouse events', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'decorated', exact: true }).click();
  const plantDensity = page.getByTestId('plant-density').getByRole('slider');
  await plantDensity.click({ position: { x: 1, y: 10 } });
  await expect(plantDensity).toHaveAttribute('aria-valuenow', '25');
  const furnitureDensity = page.getByTestId('furniture-density').getByRole('slider');
  const furnitureBox = await furnitureDensity.boundingBox();
  await furnitureDensity.click({ position: { x: furnitureBox!.width - 2, y: 10 } });
  await expect(furnitureDensity).toHaveAttribute('aria-valuenow', '200');

  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const arrangementSpacing = page.getByRole('slider', { name: 'Zur Mitte' });
  const spacingBox = await arrangementSpacing.boundingBox();
  await arrangementSpacing.click({ position: { x: spacingBox!.width - 2, y: 10 } });
  await expect(arrangementSpacing).toHaveAttribute('aria-valuenow', '20');
  const arrangementDensity = page.getByTestId('plaza-density').getByRole('slider');
  await arrangementDensity.click({ position: { x: 1, y: 10 } });
  await expect(arrangementDensity).toHaveAttribute('aria-valuenow', '25');
});

test('shipped panel CSS avoids unsupported CSS Grid', async ({ page }) => {
  const response = await page.request.get('/dist/mock.css');
  expect(response.ok()).toBeTruthy();
  const css = await response.text();
  expect(css).not.toMatch(/display:\s*grid\b/);
  expect(css).not.toMatch(/grid-template-/);
  expect(css).not.toMatch(/margin-left:\s*auto/);
});

test('the bar stays flat and keeps its groups side by side', async ({ page }) => {
  await page.goto('/');
  for (const preset of ['empty', 'decorated', 'plaza']) {
    await page.getByRole('button', { name: preset, exact: true }).click();
    const panel = await rect(page, 'park-panel');
    const header = await rect(page, 'panel-header');
    const actions = await rect(page, 'action-column');
    const columns = await page.getByTestId('panel-body').locator('section').all();
    expect(columns).toHaveLength(4);
    const boxes = await Promise.all(columns.map((column) => column.boundingBox()));
    for (let i = 1; i < boxes.length; i++) {
      expect(Math.abs(boxes[i]!.y - boxes[0]!.y)).toBeLessThan(2);
      expect(boxes[i]!.x - (boxes[i - 1]!.x + boxes[i - 1]!.width))
        .toBeGreaterThanOrEqual(7);
    }
    expect(actions.x + actions.width).toBeLessThanOrEqual(panel.x + panel.width - 7);
    expect(header.height).toBeLessThanOrEqual(44);
    // Far flatter than the old 4-step wizard; the map stays visible.
    expect(panel.height).toBeLessThanOrEqual(260);
    expect(panel.width).toBeLessThanOrEqual(1245);
    // Cohtml wraps separate text nodes, so "100" and "%" must be one node.
    const valueNodes = await page.locator('[role="slider"] + strong').evaluateAll(
      (items) => items.map((item) => item.childNodes.length));
    expect(valueNodes.every((count) => count === 1)).toBe(true);
    for (const segment of await page.getByTestId('panel-header')
      .locator('[aria-pressed]').all()) {
      if (await segment.locator('img').count() > 0) continue;
      expect((await segment.boundingBox())!.width).toBeGreaterThanOrEqual(70);
    }
  }
  await page.screenshot({ path: 'test-results/bar-layout.png' });
});

test('mock toolbar remains below the bar at compact viewport', async ({ page }) => {
  await page.setViewportSize({ width: 1059, height: 800 });
  await page.goto('/');
  for (const preset of ['decorated', 'plaza']) {
    await page.getByRole('button', { name: preset, exact: true }).click();
    const panel = await rect(page, 'park-panel');
    const toolbar = await page.locator('.mockbar').boundingBox();
    expect(toolbar!.y).toBeGreaterThanOrEqual(panel.y + panel.height);
    const overflow = await page.getByTestId('panel-body').evaluate((element) =>
      element.scrollWidth - element.clientWidth);
    expect(overflow).toBeLessThanOrEqual(1);
  }
  await page.screenshot({ path: 'test-results/compact-layout.png' });
});

test('asset chips open one category window below the bar', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'decorated', exact: true }).click();
  const chips = page.getByTestId('panel-body').locator('[data-testid^="chip-"]');
  await expect(chips).toHaveCount(6);
  await page.getByRole('button', { name: 'Bäume auswählen' }).click();
  const window = page.getByTestId('asset-window');
  await expect(window).toBeVisible();
  const bar = await rect(page, 'park-panel');
  const windowBox = (await window.boundingBox())!;
  expect(windowBox.y).toBeGreaterThanOrEqual(bar.y + bar.height + 6);
  // The dropdown spans the bar and every opener shows a caret.
  expect(Math.abs(windowBox.width - bar.width)).toBeLessThan(2);
  expect(Math.abs(windowBox.x - bar.x)).toBeLessThan(2);
  await expect(page.getByTestId('panel-body').locator('[aria-expanded] svg'))
    .toHaveCount(6);

  const treeChoices = page.getByTestId('asset-chooser').getByRole('button');
  await expect(treeChoices).toHaveCount(18);
  const iconlessTree = page.getByRole('button', { name: 'Baum 1', exact: true });
  await expect(iconlessTree.locator('img')).toHaveCount(0);
  await expect(iconlessTree.locator('svg')).toHaveCount(1);
  await page.getByRole('button', { name: 'Baum 3', exact: true }).click();
  await expect(page.getByTestId('chip-tree')).toContainText('1');

  await window.getByRole('button', { name: 'Bänke', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Bank 2', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Bänke auswählen' }))
    .toHaveAttribute('aria-expanded', 'true');
  await window.getByRole('button', { name: 'Schließen' }).click();
  await expect(window).toHaveCount(0);

  const fenceToggle = page.getByTestId('chip-fence').getByRole('button').first();
  await expect(fenceToggle).toHaveAttribute('aria-pressed', 'false');
  await fenceToggle.click();
  await expect(fenceToggle).toHaveAttribute('aria-pressed', 'true');
  await expect(mainAction(page)).toHaveText('Ausstattung planen');
});

test('plaza tile rows keep two rows and scroll instead of clipping', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const choices = page.getByTestId('plaza-center-choices');
  const first = (await choices.locator('button').first().boundingBox())!;
  const viewport = (await choices.boundingBox())!;
  expect(viewport.height).toBeLessThanOrEqual(first.height * 2 + 6);
  await choices.evaluate((element) => { element.scrollTop = element.scrollHeight; });
  const last = (await choices.locator('button').last().boundingBox())!;
  expect(last.x + last.width).toBeLessThanOrEqual(viewport.x + viewport.width);
  expect(last.y + last.height).toBeLessThanOrEqual(viewport.y + viewport.height + 1);
  await expect(page.getByTestId('plaza-center-placement').locator('button svg'))
    .toHaveCount(3);
  await expect(page.getByTestId('plaza-arrangement-placement').locator('button svg'))
    .toHaveCount(2);
});

test('park and plaza surface tiles share one size', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'paths', exact: true }).click();
  const parkSurface = page.getByTestId('park-surface-choices');
  await expect(parkSurface.locator('button')).toHaveCount(11);
  const parkTile = (await parkSurface.locator('button').first().boundingBox())!;
  const selected = parkSurface.getByRole('button', { name: 'Gras 2', exact: true });
  await selected.click();
  await expect(selected).toHaveAttribute('aria-pressed', 'true');

  await page.getByTestId('site-type-selector')
    .getByRole('button', { name: 'Plaza', exact: true }).click();
  const plazaSurface = page.getByTestId('plaza-surface-choices');
  const plazaTile = (await plazaSurface.locator('button').first().boundingBox())!;
  expect(plazaTile.width).toBeCloseTo(parkTile.width, 0);
  expect(plazaTile.height).toBeCloseTo(parkTile.height, 0);
  await expect(plazaSurface.getByRole('button', { name: 'Gras 2', exact: true }))
    .toHaveAttribute('aria-pressed', 'true');
});

test('plaza placement rules replan deterministically and optionally add a fence', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const summary = page.getByTestId('live-plan-summary');
  await expect(summary).toContainText('Plaza-Regeln');

  const centerSpacing = page.getByTestId('plaza-centerpiece-spacing').getByRole('slider');
  await expect(centerSpacing).toBeDisabled();
  const centerAxis = page.getByRole('button', { name: '3 auf Achse' });
  await centerAxis.click();
  await expect(centerAxis).toHaveAttribute('aria-pressed', 'true');
  await expect(centerSpacing).toBeEnabled();

  await page.getByRole('button', { name: 'Am Rand entlang' }).click();
  const edgeSpacing = page.getByRole('slider', { name: 'Zum Rand' });
  const sliderBox = (await edgeSpacing.boundingBox())!;
  await page.mouse.click(sliderBox.x + sliderBox.width * 0.35,
    sliderBox.y + sliderBox.height / 2);
  expect(Number(await edgeSpacing.getAttribute('aria-valuenow'))).toBeGreaterThan(0);

  const fenceChoices = page.getByTestId('plaza-fence-choices');
  await expect(fenceChoices.locator('button')).toHaveCount(16);
  const noFence = fenceChoices.getByRole('button', { name: 'Kein Zaun', exact: true });
  await expect(noFence).toHaveAttribute('aria-pressed', 'true');
  const fenceType = fenceChoices.getByRole('button', { name: 'Zaun 2', exact: true });
  await fenceType.click();
  await expect(fenceType).toHaveAttribute('aria-pressed', 'true');
  await expect(noFence).toHaveAttribute('aria-pressed', 'false');
  await expect(summary).toContainText(/[1-9]\d* Zaunläufe/);

  await noFence.click();
  await expect(noFence).toHaveAttribute('aria-pressed', 'true');
  await expect(fenceType).toHaveAttribute('aria-pressed', 'false');
  await expect(summary).toContainText('0 Zaunläufe');
});

test('plaza arrangement window shows equal slot and picker panes', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await page.getByTestId('plaza-arrangement-edit').click();
  const slots = await rect(page, 'plaza-arrangement-slots');
  const picker = await rect(page, 'plaza-arrangement-picker');
  const window = await rect(page, 'asset-window');
  expect(Math.abs(slots.height - picker.height)).toBeLessThan(2);
  expect(slots.y).toBeGreaterThan(window.y);
  expect(picker.y + picker.height).toBeLessThanOrEqual(window.y + window.height);
  await page.getByTestId('site-type-selector')
    .getByRole('button', { name: 'Park', exact: true }).click();
  await expect(page.getByTestId('asset-window')).toHaveCount(0);
});

test('park lake toggle switches the planned lake and is hidden for plazas', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'paths', exact: true }).click();
  const lake = page.getByTestId('lake-selector');
  await expect(lake).toBeVisible();
  await expect(lake).toHaveText('✓See');
  await expect(lake).toHaveAttribute('aria-pressed', 'true');
  // The lake sits as a checkbox chip in the planting chip row.
  const tree = (await page.getByTestId('chip-tree').boundingBox())!;
  const lakeBox = (await lake.boundingBox())!;
  expect(Math.abs(lakeBox.height - tree.height)).toBeLessThan(1);
  await lake.click();
  await expect(lake).toHaveAttribute('aria-pressed', 'false');
  await expect(lake).toHaveText('See');
  await lake.click();
  await expect(lake).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await expect(page.getByTestId('lake-selector')).toHaveCount(0);
});

test('the UI draws symbols as icons, not font glyphs Cohtml lacks', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await page.getByTestId('plaza-arrangement-edit').click();
  const text = await page.locator('body').innerText();
  expect(text).not.toMatch(/[↻▲▼←→✦]/);
  await expect(page.getByTestId('paths-variant').locator('svg')).toHaveCount(1);
});

test('a built park is picked on the map and removed as a whole', async ({ page }) => {
  await page.goto('/');
  // Empty workspace: the reset slot offers removing a finished park instead.
  await expect(page.getByTestId('reset-outline')).toHaveCount(0);
  await page.getByTestId('remove-mode').click();
  await expect(mainAction(page)).toHaveAttribute('data-action', 'pickPark');
  await expect(mainAction(page)).toBeDisabled();
  await page.getByRole('button', { name: 'Gebauten Park anklicken' }).click();
  await expect(mainAction(page)).toHaveAttribute('data-action', 'removePark');
  await expect(mainAction(page)).toHaveText('Park entfernen (412 Elemente)');
  await mainAction(page).click();
  // The mode stays on so several parks can be removed in a row.
  await expect(mainAction(page)).toHaveAttribute('data-action', 'pickPark');
  await page.getByTestId('remove-mode-cancel').click();
  await expect(page.getByTestId('remove-mode')).toBeVisible();
  await expect(mainAction(page)).toHaveAttribute('data-action', 'drawOutline');
});
