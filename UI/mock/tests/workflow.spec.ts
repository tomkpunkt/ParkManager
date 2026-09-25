import { expect, test, type Page } from '@playwright/test';

async function rect(page: Page, testId: string) {
  const box = await page.getByTestId(testId).boundingBox();
  expect(box, `${testId} is visible`).not.toBeNull();
  return box!;
}

async function expectNoPanelHeadingOrStageCounter(page: Page) {
  const body = page.getByTestId('panel-body');
  await expect(body.locator('h2')).toHaveCount(0);
  await expect(body.locator('.eyebrow')).toHaveCount(0);
}

test('outline clicks lead to entrances and a live path preview', async ({ page }) => {
  await page.goto('/');
  await expectNoPanelHeadingOrStageCounter(page);
  const progressSteps = page.getByTestId('workflow-progress').getByTestId('workflow-step');
  await expect(progressSteps).toHaveCount(4);
  await expect(page.getByTestId('workflow-progress').getByRole('button')).toHaveCount(0);
  await expect(progressSteps.nth(0)).toHaveAttribute('aria-current', 'step');
  await progressSteps.nth(1).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '0');
  await expect.poll(() => page.getByTestId('brand-logo').evaluate((image) =>
    (image as HTMLImageElement).complete
      && (image as HTMLImageElement).naturalWidth > 0)).toBe(true);
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
  await expectNoPanelHeadingOrStageCounter(page);
  await progressSteps.nth(2).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '1');
  await map.click({ position: { x: 610, y: 360 } });
  await expect(footer.locator('button').last()).toBeEnabled();
  await footer.locator('button').last().click();
  await expect.poll(() => page.locator('.map svg line').count()).toBeGreaterThan(0);
  await expect(page.getByText(/Live-Layout/)).toBeVisible();
});

test('path notices use explicit status and plazas call the step Structure', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await expect(page.getByTestId('workflow-progress').getByTestId('workflow-step').nth(1))
    .toContainText(/Grundstruktur|Structure/);
  await page.getByRole('button', { name: 'Fehlerstatus testen' }).click();
  const notice = page.getByRole('alert');
  await expect(notice).toHaveAttribute('data-status', 'error');
  await expect(notice).toHaveText('Validation failed: selected area is blocked.');
});

test('a plaza can be drawn, configured, built, furnished, and finished', async ({ page }) => {
  await page.goto('/');
  await expectNoPanelHeadingOrStageCounter(page);
  const map = page.locator('.map svg');
  const corners = [
    { x: 260, y: 360 }, { x: 1000, y: 360 },
    { x: 1000, y: 650 }, { x: 260, y: 650 },
  ];
  for (const corner of corners) await map.click({ position: corner });
  await map.click({ position: corners[0] });

  const footer = page.getByTestId('panel-footer');
  await footer.getByRole('button', { name: /Weiter zu den Eingängen/ }).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '1');
  await expectNoPanelHeadingOrStageCounter(page);

  const settings = page.getByTestId('path-settings');
  // Mark the entrance while the compact path layout leaves the map click area clear.
  await map.click({ position: { x: 610, y: 360 } });
  await expect(page.getByTestId('panel-body')).toContainText('1 Eingang markiert');
  await settings.getByRole('button', { name: 'Plaza', exact: true }).click();
  await expect(page.getByTestId('panel-body')).toContainText('1 Zugang markiert');
  await page.getByRole('button', { name: 'Mock Fountain', exact: true }).click();
  const fence = page.getByTestId('plaza-fence-choices')
    .getByRole('button', { name: 'Zaun 1', exact: true });
  await fence.click();
  await expect(fence).toHaveAttribute('aria-pressed', 'true');

  await footer.getByRole('button', { name: /Plaza planen/ }).click();
  await expect(page.getByTestId('live-plan-summary')).toContainText('Plaza-Regeln');
  await expect.poll(() => page.getByTestId('live-centerpiece').count())
    .toBeGreaterThan(0);
  await expect.poll(() => page.getByTestId('live-furniture').count())
    .toBeGreaterThan(0);
  await expect.poll(() => page.getByTestId('live-plaza-fence').count())
    .toBeGreaterThan(0);

  await footer.getByRole('button', { name: 'Plaza bauen' }).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '2');
  await expectNoPanelHeadingOrStageCounter(page);
  const removeSurface = footer.getByRole('button', { name: 'Untergrund entfernen' });
  await expect(removeSurface).toHaveClass(/backButton/);
  await expect(removeSurface).not.toHaveClass(/dangerButton/);
  await expect(removeSurface.locator('img')).toHaveCSS('transform', 'matrix(-1, 0, 0, -1, 0, 0)');
  const slots = page.getByTestId('plaza-arrangement-slots');
  await slots.getByTitle('Element hinzufügen').click();
  await page.getByTestId('plaza-arrangement-picker')
    .getByRole('button', { name: 'Laternen', exact: true }).click();
  await page.getByTestId('plaza-arrangement-picker')
    .getByRole('button', { name: 'Laterne 1', exact: true }).click();
  await expect(slots.getByRole('button').nth(1)).toContainText('Laterne 1');

  await footer.getByRole('button', { name: 'Ausstattung bauen' }).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '3');
  await expectNoPanelHeadingOrStageCounter(page);
  await expect(page.getByTestId('panel-body')).toContainText(/Plaza-Untergrund/);
  await footer.getByRole('button', { name: 'Plaza fertigstellen' }).click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '0');
  await expect(page.getByText('Noch keine Punkte gesetzt')).toBeVisible();
});

test('plaza preview follows placement rules instead of changing randomly', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const summary = page.getByTestId('live-plan-summary');
  await expect(summary).toContainText('Plaza-Regeln');
  const refresh = page.getByRole('button', { name: 'Vorschau aktualisieren' });
  await refresh.click();
  await expect.poll(() => page.getByTestId('live-centerpiece').count()).toBeGreaterThan(0);
  await expect.poll(() => page.getByTestId('live-furniture').count()).toBeGreaterThan(0);
  expect(await page.locator('.map svg line').count()).toBe(0);
  const firstLayout = await page.getByTestId('live-centerpiece').evaluateAll((items) =>
    items.map((item) => [item.getAttribute('cx'), item.getAttribute('cy')].join(',')).join(';'));
  const refreshedPlan = page.waitForResponse((response) =>
    response.url().endsWith('/api/plan'));
  await refresh.click();
  await refreshedPlan;
  await expect.poll(() => page.getByTestId('live-centerpiece').evaluateAll((items) =>
    items.map((item) => [item.getAttribute('cx'), item.getAttribute('cy')].join(',')).join(';')))
    .toBe(firstLayout);
});

test('shared plaza sliders support arrow, home, end, and page keys', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const slider = page.getByRole('slider', { name: 'Abstand zum Zentrum' });
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

test('path panel text aligns top while Plaza selectors remain at the bottom', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'paths', exact: true }).click();

  const copy = page.getByTestId('path-stage-copy');
  const alignment = await copy.evaluate((element) => {
    const style = getComputedStyle(element);
    return { alignSelf: style.alignSelf, justifyContent: style.justifyContent };
  });
  expect(alignment).toEqual({ alignSelf: 'stretch', justifyContent: 'space-between' });

  const parkCopy = await rect(page, 'path-stage-copy');
  const parkIntro = await rect(page, 'path-stage-intro');
  const parkState = await rect(page, 'path-stage-state');
  expect(parkIntro.y).toBeCloseTo(parkCopy.y, 0);
  expect(parkState.y).toBeGreaterThanOrEqual(parkIntro.y);

  await page.getByTestId('path-settings')
    .getByRole('button', { name: 'Plaza', exact: true }).click();
  const plazaCopy = await rect(page, 'path-stage-copy');
  const plazaIntro = await rect(page, 'path-stage-intro');
  const plazaSelectors = await rect(page, 'plaza-asset-selectors');
  const plazaCenter = await rect(page, 'plaza-center-group');
  const plazaFence = await rect(page, 'plaza-fence-group');
  expect(plazaIntro.y).toBeCloseTo(plazaCopy.y, 0);
  expect(plazaSelectors.y + plazaSelectors.height).toBeCloseTo(
    plazaCopy.y + plazaCopy.height, 0);
  expect(plazaCenter.y + plazaCenter.height).toBeLessThanOrEqual(plazaFence.y);
  expect(plazaFence.y + plazaFence.height).toBeCloseTo(
    plazaSelectors.y + plazaSelectors.height, 0);
});

test('Park and Plaza path steps share the same settings column scale', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'paths', exact: true }).click();
  const settings = page.getByTestId('path-settings');
  const firstSetting = settings.locator(':scope > div').first();
  const readLayout = async () => ({
    labelWidth: (await firstSetting.locator('span').first().boundingBox())!.width,
    controlHeight: (await firstSetting.boundingBox())!.height,
    settingsSpacing: await settings.evaluate((element) => {
      const first = element.children[0].getBoundingClientRect();
      const second = element.children[1].getBoundingClientRect();
      return second.top - first.bottom;
    }),
  });
  const park = await readLayout();
  await settings.getByRole('button', { name: 'Plaza', exact: true }).click();
  const plaza = await readLayout();

  expect(plaza).toEqual(park);
  expect(park.labelWidth).toBeGreaterThan(170);
  expect(park.controlHeight).toBeGreaterThanOrEqual(39);
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

test('plaza fence sits left and the surface selector follows geometry', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const leftSelectors = await rect(page, 'plaza-asset-selectors');
  const centerAssets = await rect(page, 'plaza-center-choices');
  const surfaceAssets = await rect(page, 'plaza-surface-choices');
  const fenceAssets = await rect(page, 'plaza-fence-choices');
  const settingsPanel = page.getByTestId('path-settings');
  const settings = await rect(page, 'path-settings');
  const centerTile = await page.getByTestId('plaza-center-choices')
    .locator('button').first().boundingBox();
  const surfaceTile = await page.getByTestId('plaza-surface-choices')
    .locator('button').first().boundingBox();
  const centerMode = page.getByTestId('plaza-center-placement');
  const arrangementMode = page.getByTestId('plaza-arrangement-placement');
  const arrangementModeBox = await arrangementMode.boundingBox();
  const centerSpacing = await page.getByRole('slider', {
    name: 'Abstand der Mittelobjekte' }).boundingBox();

  expect(leftSelectors.x + leftSelectors.width).toBeLessThanOrEqual(settings.x);
  expect(centerAssets.x).toBeLessThan(settings.x);
  expect(fenceAssets.x).toBeLessThan(settings.x);
  expect(surfaceAssets.x).toBeGreaterThanOrEqual(settings.x);
  await expect(page.getByTestId('plaza-asset-selectors')
    .getByTestId('plaza-fence-group')).toBeVisible();
  await expect(settingsPanel.getByTestId('plaza-surface-group')).toBeVisible();
  expect(await settingsPanel.getByTestId('plaza-surface-group').count()).toBe(1);
  expect(centerTile?.width).toBeCloseTo(36, 0);
  expect(centerTile?.height).toBeCloseTo(36, 0);
  expect(surfaceTile?.width).toBeCloseTo(36, 0);
  expect(surfaceTile?.height).toBeCloseTo(36, 0);
  const fenceTile = await page.getByTestId('plaza-fence-choices')
    .locator('button').first().boundingBox();
  expect(fenceTile?.width).toBeCloseTo(36, 0);
  expect(fenceTile?.height).toBeCloseTo(36, 0);
  expect(await page.getByTestId('plaza-asset-selectors').locator('svg').count()).toBe(0);
  expect(await settingsPanel.locator('svg').count()).toBe(5);
  const backIcon = page.getByRole('button', { name: 'Umriss bearbeiten' })
    .locator('img');
  await expect(backIcon).toHaveCount(1);
  await expect(backIcon).toHaveCSS('transform', 'matrix(-1, 0, 0, -1, 0, 0)');
  expect(arrangementModeBox!.y).toBeGreaterThanOrEqual(
    centerSpacing!.y + centerSpacing!.height);
  expect(await centerMode.locator('button svg').count()).toBe(3);
  expect(await arrangementMode.locator('button svg').count()).toBe(2);
  await page.screenshot({ path: 'test-results/plaza-layout.png' });
});

test('park and plaza surface selectors share the Plaza layout without info badges', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'paths', exact: true }).click();

  const parkSurface = page.getByTestId('park-surface-choices');
  const parkHeader = page.getByTestId('park-surface-group-header');
  const pathSettings = page.getByTestId('path-settings');
  const parkPathTypeBox = await pathSettings.getByTestId('path-width-selector').boundingBox();
  const parkDividerBox = await pathSettings.getByTestId('path-surface-divider').boundingBox();
  const parkSurfaceGroupBox = await page.getByTestId('park-surface-group').boundingBox();
  await expect(parkSurface).toBeVisible();
  await expect(pathSettings.getByTestId('path-surface-divider'))
    .toHaveAttribute('role', 'separator');
  await expect(parkSurface.locator('button')).toHaveCount(11);
  await expect(parkHeader.locator('span')).toHaveCount(0);
  await expect(page.getByTestId('park-asset-selectors')).toHaveCount(0);
  expect(parkDividerBox!.y).toBeGreaterThanOrEqual(
    parkPathTypeBox!.y + parkPathTypeBox!.height);
  expect(parkSurfaceGroupBox!.y).toBeGreaterThanOrEqual(
    parkDividerBox!.y + parkDividerBox!.height);
  expect(parkSurfaceGroupBox!.x).toBeGreaterThanOrEqual(
    (await pathSettings.boundingBox())!.x);
  const parkTile = await parkSurface.locator('button').first().boundingBox();
  const parkGap = await parkSurface.evaluate((element) => getComputedStyle(element).gap);
  expect(parkTile?.width).toBeCloseTo(36, 0);
  expect(parkTile?.height).toBeCloseTo(36, 0);

  const selectedSurface = parkSurface.getByRole('button', { name: 'Gras 1', exact: true });
  await selectedSurface.click();
  await expect(selectedSurface).toHaveAttribute('aria-pressed', 'true');

  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const plazaSurface = page.getByTestId('plaza-surface-choices');
  const plazaHeader = page.getByTestId('plaza-surface-group-header');
  await expect(plazaSurface).toBeVisible();
  await expect(plazaHeader.locator('span')).toHaveCount(0);
  await expect(page.getByTestId('plaza-center-header').locator('span'))
    .toHaveCount(0);
  const plazaTile = await plazaSurface.locator('button').first().boundingBox();
  const plazaGap = await plazaSurface.evaluate((element) => getComputedStyle(element).gap);
  expect(plazaTile?.width).toBeCloseTo(parkTile!.width, 0);
  expect(plazaTile?.height).toBeCloseTo(parkTile!.height, 0);
  expect(plazaGap).toBe(parkGap);
  await expect(plazaSurface.getByRole('button', { name: 'Gras 1', exact: true }))
    .toHaveAttribute('aria-pressed', 'true');
});

test('plaza placement rules replan deterministically and optionally add a fence', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  const summary = page.getByTestId('live-plan-summary');
  await expect(summary).toContainText('Plaza-Regeln');

  const centerAxis = page.getByRole('button', { name: '3 auf Achse' });
  await centerAxis.click();
  await expect(centerAxis).toHaveAttribute('aria-pressed', 'true');
  const centerSpacing = page.getByRole('slider', { name: 'Abstand der Mittelobjekte' });
  expect(await centerSpacing.isDisabled()).toBe(false);

  await page.getByRole('button', { name: 'Am Rand entlang' }).click();
  const edgeSpacing = page.getByRole('slider', { name: 'Abstand zum Rand' });
  const sliderBox = await edgeSpacing.boundingBox();
  expect(sliderBox).not.toBeNull();
  await page.mouse.click(sliderBox!.x + sliderBox!.width * 0.35,
    sliderBox!.y + sliderBox!.height / 2);
  expect(Number(await edgeSpacing.getAttribute('aria-valuenow'))).toBeGreaterThan(0);

  const fenceChoices = page.getByTestId('plaza-fence-choices');
  await expect(fenceChoices).toBeVisible();
  await expect(fenceChoices.locator('button')).toHaveCount(16);
  const settingsSpacing = await page.getByTestId('path-settings').evaluate((element) => {
    const children = Array.from(element.children) as HTMLElement[];
    return {
      gap: getComputedStyle(element).rowGap,
      margins: children.map((child) => getComputedStyle(child).marginBottom),
    };
  });
  expect(settingsSpacing.gap).toBe('0px');
  expect(new Set(settingsSpacing.margins.slice(0, -1)).size).toBe(1);
  expect(settingsSpacing.margins[0]).not.toBe('0px');
  expect(settingsSpacing.margins.at(-1)).toBe('0px');
  const dividers = page.locator('[data-testid$="-divider"]');
  await expect(dividers).toHaveCount(3);
  for (const divider of await dividers.all()) {
    await expect(divider).toBeVisible();
    const dividerSpacing = await divider.evaluate((element) => {
      const dividerStyle = getComputedStyle(element);
      const dividerBox = element.getBoundingClientRect();
      const nextBox = element.nextElementSibling!.getBoundingClientRect();
      return {
        height: dividerBox.height,
        marginBottom: parseFloat(dividerStyle.marginBottom),
        gapBelow: nextBox.top - dividerBox.bottom,
      };
    });
    expect(dividerSpacing.height).toBeGreaterThan(0);
    expect(dividerSpacing.marginBottom).toBeGreaterThan(0);
    expect(dividerSpacing.gapBelow).toBeCloseTo(dividerSpacing.marginBottom, 1);
  }
  const noFence = fenceChoices.getByRole('button', { name: 'Kein Zaun', exact: true });
  await expect(noFence).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: 'Randzaun hinzufügen' })).toHaveCount(0);
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

test('plaza detail panes have equal constrained height and one-line density', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await page.getByTestId('panel-footer').locator('button').last().click();
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

test('Plaza detail header has no extra height while panes and density stay aligned', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'decorated', exact: true }).click();
  const parkBody = await rect(page, 'panel-body');
  const parkHeader = await rect(page, 'asset-header');
  const parkWorkspace = await rect(page, 'asset-workspace');
  const parkGrid = await rect(page, 'asset-grid');
  const parkChooser = await rect(page, 'asset-chooser');
  const parkDensity = await page.getByRole('slider').first()
    .evaluate((element) => element.parentElement!.getBoundingClientRect().toJSON());

  await page.getByRole('button', { name: 'plaza', exact: true }).click();
  await page.getByTestId('panel-footer').locator('button').last().click();
  await expect(page.getByTestId('panel-body')).toHaveAttribute('data-stage', '2');
  const plazaBody = await rect(page, 'panel-body');
  const plazaHeader = await rect(page, 'asset-header');
  const plazaWorkspace = await rect(page, 'plaza-arrangement-workspace');
  const plazaSlots = await rect(page, 'plaza-arrangement-slots');
  const plazaPicker = await rect(page, 'plaza-arrangement-picker');
  const plazaDensity = await rect(page, 'plaza-density-settings');
  const plazaHeaderGap = plazaWorkspace.y - (plazaHeader.y + plazaHeader.height);

  expect(Math.abs(parkBody.height - plazaBody.height)).toBeLessThanOrEqual(10);
  expect(plazaHeader.height).toBeLessThan(parkHeader.height - 20);
  expect(plazaHeaderGap).toBeCloseTo(10, 0);
  expect(Math.abs(parkWorkspace.height - plazaWorkspace.height)).toBeLessThan(2);
  expect(Math.abs(parkGrid.height - parkChooser.height)).toBeLessThan(2);
  expect(Math.abs(plazaSlots.height - plazaPicker.height)).toBeLessThan(2);
  expect(Math.abs(parkGrid.width - parkChooser.width)).toBeLessThan(2);
  expect(Math.abs(plazaSlots.width - plazaPicker.width)).toBeLessThan(2);
  expect(Math.abs(parkDensity!.height - plazaDensity.height)).toBeLessThan(2);
});
