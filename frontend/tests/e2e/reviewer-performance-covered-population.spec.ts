// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { expect, test, type Page } from '@playwright/test'
import type { PerformanceResponse } from '../../src/services/reviewerPerformanceService'

async function openPerformance(page: Page): Promise<PerformanceResponse> {
  await page.goto('/login')
  await page.getByLabel('Username').fill('admin')
  await page.getByLabel('Password').fill('admin')
  await page.locator('form.login-form button[type="submit"]').click()
  await page.waitForURL('**/clients')
  const loaded = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await page.goto('/reviewer-performance')
  const result = await (await loaded).json() as PerformanceResponse
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  await expect(page.locator('.measurement-coverage').first()).toBeVisible()
  await page.evaluate(() => document.fonts.ready)
  return result
}

test('shows qualified model F1 and source-scoped local recovery while preserving snapshots', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses owned model opportunity fixtures.')
  await page.clock.setFixedTime(new Date('2026-10-18T12:00:00Z'))
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  const loaded = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await page.getByRole('combobox', { name: 'Separate series', exact: true }).press('ArrowDown')
  await page.getByRole('option', { name: 'Models', exact: true }).click()
  const result = await (await loaded).json() as PerformanceResponse
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  const qualified = result.views![0]!.series!.find((series) => series.label === 'review-small')!
  expect(qualified.points!.at(-1)!.score!.summary!.f1!.median).toBeGreaterThan(0)
  await page.getByRole('combobox', { name: 'Inspect series', exact: true }).press('ArrowDown')
  await page.getByRole('option', { name: 'review-small', exact: true }).click()
  await page.keyboard.press('Escape')
  const summary = page.locator('.measurement-coverage')
  await expect(summary).toContainText('F1 is available')
  await expect(summary).toContainText('Models can cover different workloads')
  await page.screenshot({ path: testInfo.outputPath('qualified-model-f1.png'), fullPage: true })

  await page.getByRole('combobox', { name: 'Inspect series', exact: true }).press('ArrowDown')
  await page.getByRole('option', { name: 'review-next', exact: true }).click()
  await page.keyboard.press('Escape')
  await summary.getByRole('button', { name: /View exclusions/ }).click()
  const dialog = page.getByRole('dialog', { name: 'Excluded pull requests', exact: true })
  await expect(dialog).toContainText('Historical model evidence cannot be reconstructed')
  let local = dialog.locator('tbody tr').filter({ has: page.getByRole('button', { name: 'Retry retained classification', exact: true }) }).first()
  for (let pageNumber = 0; pageNumber < 8 && await local.count() === 0; pageNumber++) {
    const next = dialog.getByRole('button', { name: 'Next exclusions page', exact: true })
    if (await next.isDisabled()) break
    await next.click()
    local = dialog.locator('tbody tr').filter({ has: page.getByRole('button', { name: 'Retry retained classification', exact: true }) }).first()
  }
  await expect(local).toBeVisible()
  const entryId = Number(await local.getAttribute('data-exclusion-id'))
  local = dialog.locator(`tbody tr[data-exclusion-id="${entryId}"]`)
  const entry = result.views![0]!.exclusionCatalog!.find((entry) => entry.id === entryId)!
  const retry = page.waitForResponse((response) => response.url().endsWith('/retry-model'))
  await local.getByRole('button', { name: 'Retry retained classification', exact: true }).click()
  const response = await retry
  expect(response.status()).toBe(200)
  expect(response.request().postDataJSON()).toEqual({ providerScope: entry.providerScope, connectionId: entry.sourceConnectionId })
  await expect(local).toContainText('Classification attempt completed')
  await expect(local).not.toContainText('Queued for a background sweep')
  await page.screenshot({ path: testInfo.outputPath('model-local-classification.png') })
  await dialog.getByRole('button', { name: 'Close exclusions' }).click()
  const filters = page.getByRole('button', { name: 'Filters', exact: true })
  if (await filters.getAttribute('aria-expanded') === 'false') await filters.click()
  await page.getByRole('button', { name: 'Save snapshot', exact: true }).click()
  await page.getByLabel('Snapshot name', { exact: true }).fill('Qualified model capture')
  await page.getByRole('button', { name: 'Capture and save', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Saved: Qualified model capture' })).toBeVisible()
  await summary.getByRole('button', { name: /View exclusions/ }).click()
  await expect(dialog).toContainText('Captured details')
  await expect(dialog.getByRole('button', { name: 'Retry retained classification' })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Requeue collection' })).toHaveCount(0)
})

test('distinguishes certified zero model recall from an inactive zero-result period', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses owned zero-result opportunity fixtures.')
  await page.clock.setFixedTime(new Date('2026-10-18T12:00:00Z'))
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  const periodRead = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await page.getByRole('combobox', { name: 'Aggregation', exact: true }).press('ArrowDown')
  await page.getByRole('option', { name: 'Per period', exact: true }).click()
  await periodRead
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  const filters = page.getByRole('button', { name: 'Filters', exact: true })
  if (await filters.getAttribute('aria-expanded') === 'false') await filters.click()
  await page.getByRole('combobox', { name: 'Models', exact: true }).fill('review-next')
  await page.getByRole('option', { name: 'review-next', exact: true }).click()
  await page.keyboard.press('Escape')
  await page.getByRole('combobox', { name: 'Repositories', exact: true }).fill('Payments API')
  await page.getByRole('option', { name: 'Payments API', exact: true }).click()
  await page.keyboard.press('Escape')
  const applied = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await page.getByRole('button', { name: 'Apply filters', exact: true }).click()
  const result = await (await applied).json() as PerformanceResponse
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  const points = result.views![0]!.series![0]!.points!
  const zero = points.findIndex((point) => point.score?.summary?.f1?.median === 0 && point.score.summary.precision == null)
  const inactive = points.findIndex((point) => point.coverage?.coveredPullRequests === 1 && point.score?.summary?.f1 == null
    && point.score?.counts?.outcomes?.total === 0 && point.score?.counts?.actedMisses === 0)
  expect(zero).toBeGreaterThanOrEqual(0)
  expect(inactive).toBeGreaterThanOrEqual(0)
  const chart = page.getByRole('group', { name: 'Inspect reviewer performance chart', exact: true })
  const summary = page.getByRole('region', { name: 'Selected point coverage', exact: true })
  for (const [index, active] of [[zero, true], [inactive, false]] as const) {
    await chart.focus()
    await chart.press('Home')
    for (let i = 0; i < index; i++) await chart.press('ArrowRight')
    await chart.press('Enter')
    await expect(summary).toContainText(active ? 'F1 is available' : 'no scored outcomes or settled human misses')
    if (active) await expect(page.getByRole('tooltip', { name: 'Measurement details', exact: true })).toContainText('0.0%')
    await page.screenshot({ path: testInfo.outputPath(active ? 'certified-model-zero.png' : 'inactive-model-zero.png'), fullPage: true })
  }
})

test('inspects captured coverage, pages exclusions locally and preserves scoped snapshots', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses retained mock evidence and saved reports.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  const result = await openPerformance(page)
  const latest = result.views![0]!.series![0]!.points!.at(-1)!
  const summary = page.getByRole('region', { name: 'Window end coverage', exact: true })
  await expect(summary).toContainText(`${latest.coverage!.coveredPullRequests} of ${latest.coverage!.candidatePullRequests} PRs covered`)
  await expect(summary).toContainText('F1 is available')
  await page.screenshot({ path: testInfo.outputPath('covered-population-desktop.png'), fullPage: true })

  let reads = 0
  page.on('request', (request) => { if (request.url().includes('/reviewer-performance/ranges/query')) reads++ })
  const trigger = summary.getByRole('button', { name: /View exclusions/ })
  await trigger.click()
  const dialog = page.getByRole('dialog', { name: 'Excluded pull requests', exact: true })
  const close = dialog.getByRole('button', { name: 'Close exclusions' })
  await expect(close).toBeFocused()
  await expect(dialog.locator('[data-exclusion-id="0"]')).toBeVisible()
  await expect(dialog).toContainText('AzureDevOps:https://dev.azure.com/example')
  await page.screenshot({ path: testInfo.outputPath('exclusions-desktop.png') })
  await page.keyboard.press('Shift+Tab')
  await expect(dialog.getByRole('button', { name: 'Refresh current evidence' })).toBeFocused()
  await page.screenshot({ path: testInfo.outputPath('exclusions-keyboard-focus.png') })
  await dialog.getByRole('button', { name: 'Next exclusions page' }).click()
  await expect(dialog.locator('caption')).toContainText('page 2')
  expect(reads).toBe(0)
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(trigger).toBeFocused()

  const filters = page.getByRole('button', { name: 'Filters', exact: true })
  if (await filters.getAttribute('aria-expanded') === 'false') await filters.click()
  const clients = page.getByRole('combobox', { name: 'Clients', exact: true })
  await clients.fill('Payments')
  await page.getByRole('option', { name: 'Payments', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(page.getByText('Scope edits are pending.', { exact: false })).toBeVisible()
  expect(reads).toBe(0)
  const applied = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await page.getByRole('button', { name: 'Apply filters', exact: true }).click()
  const scoped = await (await applied).json() as PerformanceResponse
  await expect(page.getByRole('status').filter({ hasText: 'Loading retained evidence' })).toHaveCount(0)
  await page.getByRole('button', { name: /View exclusions/ }).click()
  await expect(dialog.locator('tbody')).not.toContainText('Platform')
  expect(scoped.views![0]!.exclusionCatalog!.every((entry) => entry.clientName === 'Payments')).toBe(true)
  await dialog.getByRole('button', { name: 'Close exclusions' }).click()
  await page.getByRole('button', { name: 'Save snapshot', exact: true }).click()
  await page.getByLabel('Snapshot name', { exact: true }).fill('Covered population capture')
  await page.getByRole('button', { name: 'Capture and save', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Saved: Covered population capture' })).toBeVisible()
  const readsBeforeFrozen = reads
  await page.getByRole('button', { name: /View exclusions/ }).click()
  await expect(dialog).toContainText('Captured details')
  await expect(dialog.getByRole('button', { name: 'Requeue collection' })).toHaveCount(0)
  expect(reads).toBe(readsBeforeFrozen)
})

test('shows actionable recovery errors and queues live collection without changing captured details', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses the mock retry endpoint.')
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  await page.getByRole('button', { name: /View exclusions/ }).click()
  const dialog = page.getByRole('dialog', { name: 'Excluded pull requests', exact: true })
  const row = dialog.locator('tbody tr').first()
  const before = await row.getByRole('cell').nth(2).innerText()
  await page.evaluate(() => {
    const original = window.fetch.bind(window)
    window.fetch = (input, init) => {
      const url = input instanceof Request ? input.url : String(input)
      if (url.includes('/reviewer-performance/evidence/') && url.endsWith('/retry')) {
        window.fetch = original
        return Promise.resolve(new Response(JSON.stringify({ title: 'Evidence observation is in progress', detail: 'Wait for the current evidence observation to finish, then retry.' }), { status: 409, headers: { 'Content-Type': 'application/problem+json' } }))
      }
      return original(input, init)
    }
  })
  await row.getByRole('button', { name: 'Requeue collection' }).click()
  await expect(row.getByRole('alert')).toContainText('Wait for the current evidence observation')
  await page.screenshot({ path: testInfo.outputPath('exclusions-recovery-error.png') })
  const queued = page.waitForResponse((response) => response.url().includes('/reviewer-performance/evidence/') && response.url().endsWith('/retry'))
  await row.getByRole('button', { name: 'Requeue collection' }).click()
  const response = await queued
  expect(response.status()).toBe(202)
  expect(response.request().postData()).toBeNull()
  await expect(row.getByRole('status')).toContainText('Queued for a background sweep')
  expect(await row.getByRole('cell').nth(2).innerText()).toBe(before)
  await page.screenshot({ path: testInfo.outputPath('exclusions-recovery-queued.png') })
})

test('shows below-minimum F1 and preserves collection progress across reopen and refresh', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses retained mock evidence and deterministic collection progress.')
  await page.clock.setFixedTime(new Date('2026-10-18T12:00:00Z'))
  await page.setViewportSize({ width: 1440, height: 1080 })
  await openPerformance(page)
  const changed = page.waitForResponse((response) => response.request().method() === 'POST' && response.url().includes('/reviewer-performance/ranges/query'))
  await page.getByRole('combobox', { name: 'Aggregation', exact: true }).press('ArrowDown')
  await page.getByRole('option', { name: 'Per period', exact: true }).click()
  const period = await (await changed).json() as PerformanceResponse
  const series = period.views![0]!.series![0]!
  const selected = series.points!.find((point) => point.coverage?.meetsMinimum === false && point.score?.summary?.f1?.median != null)!
  const chart = page.getByRole('group', { name: 'Inspect reviewer performance chart', exact: true })
  await chart.focus()
  await page.keyboard.press('Home')
  for (let index = 0; index < series.points!.indexOf(selected); index++) await page.keyboard.press('ArrowRight')
  await page.keyboard.press('Enter')
  const summary = page.getByRole('region', { name: 'Selected point coverage', exact: true })
  await expect(summary).toContainText('F1 is available')
  await expect(summary).toContainText('Coverage is below the advisory minimum')
  await summary.getByRole('button', { name: /View exclusions/ }).click()
  const dialog = page.getByRole('dialog', { name: 'Excluded pull requests', exact: true })
  const first = dialog.locator('tbody tr').first()
  const excluded = selected.coverage!.excludedPullRequests!
  await first.getByRole('button', { name: 'Requeue collection' }).click()
  await expect(first).toContainText('Collection queued')
  await dialog.getByRole('button', { name: 'Close exclusions' }).click()
  await summary.getByRole('button', { name: /View exclusions/ }).click()
  await expect(dialog).toContainText('Collection queued')
  await dialog.getByRole('button', { name: 'Refresh current evidence' }).click()
  await expect(dialog.getByRole('button', { name: 'Refresh current evidence' })).toBeEnabled()
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText(selected.windowFrom!)
  await expect(dialog).toContainText(selected.windowTo!)
  await expect(dialog).toContainText('Queued / eligible for a background sweep from')
  await expect(summary).toContainText(`${excluded} excluded`)
  await page.screenshot({ path: testInfo.outputPath('below-minimum-queued-progress.png') })
  await page.clock.setFixedTime(new Date('2026-10-18T12:00:06Z'))
  await dialog.getByRole('button', { name: 'Refresh current evidence' }).click()
  await expect(dialog).toContainText('Collection in progress')
  await expect(dialog).toContainText('Observation lease expires')
  await expect(summary).toContainText(`${excluded} excluded`)
  await page.screenshot({ path: testInfo.outputPath('below-minimum-running-progress.png') })
})

test('keeps mobile exclusion details inside the viewport and restores keyboard focus', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mock', 'Uses retained mock evidence.')
  await page.setViewportSize({ width: 390, height: 844 })
  await openPerformance(page)
  const trigger = page.getByRole('button', { name: /View exclusions/ })
  await trigger.click()
  const dialog = page.getByRole('dialog', { name: 'Excluded pull requests', exact: true })
  await expect(dialog.getByRole('button', { name: 'Close exclusions' })).toBeFocused()
  const bounds = (await dialog.boundingBox())!
  expect(bounds.x).toBeGreaterThanOrEqual(0)
  expect(bounds.width).toBeLessThanOrEqual(390)
  expect(bounds.y + bounds.height).toBeLessThanOrEqual(844)
  expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true)
  const caption = (await dialog.locator('caption').boundingBox())!
  expect(caption.width).toBeGreaterThan(bounds.width - 70)
  await expect(dialog.locator('tbody tr').first()).toContainText(/Complete human-thread collection|human-thread judgement failed/)
  await page.screenshot({ path: testInfo.outputPath('exclusions-mobile.png') })
  await page.keyboard.press('Escape')
  await expect(dialog).toHaveCount(0)
  await expect(trigger).toBeFocused()
})
