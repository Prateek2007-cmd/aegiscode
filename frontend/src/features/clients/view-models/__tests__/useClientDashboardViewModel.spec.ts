// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { useClientDashboardViewModel } from '../useClientDashboardViewModel'
import type { ClientDetailDto } from '../useClientDetailViewModel'
import type { ClientListItem } from '../useClientsViewModel'
import type {
  PagedSecurityFindings,
  SecurityFinding,
  SecurityScan,
  SecurityScanSummary,
} from '@/features/security-scans/types'
import { FixProposalsUnavailableError, type FixProposal } from '@/services/fixProposalsService'
import type { PullRequestHistoryItem, PullRequestHistoryResponse } from '@/services/jobsService'

function client(partial: Partial<ClientDetailDto> = {}): ClientDetailDto {
  return {
    id: 'client-1',
    displayName: 'Acme',
    isActive: true,
    createdAt: '2026-01-01T00:00:00Z',
    scmCommentPostingEnabled: true,
    enableMultiPassUnion: false,
    reviewEveryIncrementEnabled: false,
    includeLinkedItemsInContext: true,
    ...partial,
  }
}

function summary(partial: Partial<SecurityScanSummary> = {}): SecurityScanSummary {
  return { critical: 0, high: 0, medium: 0, low: 0, info: 0, ...partial }
}

function scan(partial: Partial<SecurityScan> = {}): SecurityScan {
  return {
    scanId: 'scan-1',
    clientId: 'client-1',
    repositoryUrl: 'https://github.com/acme/repo',
    status: 'Succeeded',
    progressPct: 100,
    queuedAt: '2026-10-25T10:00:00Z',
    startedAt: '2026-10-25T10:01:00Z',
    finishedAt: '2026-10-25T10:05:00Z',
    error: null,
    coverage: null,
    summary: summary(),
    ...partial,
  }
}

function finding(partial: Partial<SecurityFinding> = {}): SecurityFinding {
  return {
    id: 'f1',
    scanner: 'dep-scan',
    ruleId: null,
    severity: 'High',
    priorityScore: 70,
    scoreGaps: [],
    title: 'test finding',
    category: 'test',
    filePath: null,
    line: null,
    evidence: null,
    remediation: null,
    confidence: 'High',
    status: 'Open',
    firstSeenAt: '2026-10-25T00:00:00Z',
    lastSeenAt: '2026-10-25T00:00:00Z',
    ...partial,
  }
}

function findingsPage(items: SecurityFinding[]): PagedSecurityFindings {
  return { items, totalCount: items.length }
}

function reviewGroup(partial: Partial<PullRequestHistoryItem> = {}): PullRequestHistoryItem {
  return {
    providerScopePath: 'https://github.com',
    providerProjectKey: 'acme',
    repositoryId: 'repo-1',
    pullRequestId: 7,
    clientId: 'client-1',
    prTitle: 'Fix the thing',
    prRepositoryName: 'repo-1',
    prSourceBranch: 'feature',
    prTargetBranch: 'main',
    latestActivityAt: '2026-10-25T12:00:00Z',
    totalInputTokens: 100,
    totalOutputTokens: 10,
    totalEstimatedCostUsd: 0.5,
    costIsApproximate: false,
    jobs: [
      {
        id: 'job-1',
        clientId: 'client-1',
        status: 'completed',
        submittedAt: '2026-10-25T11:00:00Z',
        completedAt: '2026-10-25T12:00:00Z',
        pullRequestId: 7,
      },
    ],
    ...partial,
  } as PullRequestHistoryItem
}

function historyResponse(items: PullRequestHistoryItem[], total: number): PullRequestHistoryResponse {
  return { total, items }
}

function proposal(partial: Partial<FixProposal> = {}): FixProposal {
  return {
    id: 'prop-1',
    findingRef: 'scan:scan-1:f1',
    source: 'scan',
    title: 'Fix it',
    filePath: null,
    line: null,
    remediation: null,
    confidence: null,
    status: 'PendingApproval',
    createdAt: '2026-10-25T09:00:00Z',
    decidedAt: null,
    decisionReason: null,
    ...partial,
  }
}

function listItem(partial: Partial<ClientListItem> = {}): ClientListItem {
  return {
    id: 'client-1',
    displayName: 'Acme',
    isActive: true,
    createdAt: '2026-01-01T00:00:00Z',
    tenantDisplayName: 'Acme Tenant',
    ...partial,
  }
}

/** Every service stubbed with happy-path data; individual tests override the seam they exercise. */
function happyService(overrides: Record<string, unknown> = {}) {
  return {
    getClient: vi.fn().mockResolvedValue({ data: client(), response: { status: 200, ok: true } }),
    listClients: vi.fn().mockResolvedValue([listItem()]),
    listSecurityScans: vi.fn().mockResolvedValue([scan()]),
    getScanFindings: vi.fn().mockResolvedValue(findingsPage([finding()])),
    listFixProposals: vi.fn().mockResolvedValue([proposal()]),
    listPullRequestHistory: vi.fn().mockResolvedValue(historyResponse([reviewGroup()], 3)),
    ...overrides,
  }
}

describe('useClientDashboardViewModel header', () => {
  it('loads the client and resolves the tenant name from the clients list', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService(),
    })

    await vm.refresh()

    expect(vm.client?.displayName).toBe('Acme')
    expect(vm.tenantDisplayName).toBe('Acme Tenant')
    expect(vm.loading.value).toBe(false)
    expect(vm.loadError.value).toBe('')
    expect(vm.notFound.value).toBe(false)
  })

  it('reports notFound on a 404 and leaves the tenant name null when the list load fails', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        getClient: vi.fn().mockResolvedValue({ data: null, response: { status: 404, ok: false } }),
        listClients: vi.fn().mockRejectedValue(new Error('boom')),
      }),
    })

    await vm.refresh()

    expect(vm.notFound.value).toBe(true)
    expect(vm.client).toBeNull()
    // A failed tenant lookup never fabricates a name and never fails the header.
    expect(vm.tenantDisplayName).toBeNull()
    expect(vm.loadError.value).toBe('')
  })

  it('reports a load error (not notFound) when the client request fails without a body', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        getClient: vi.fn().mockRejectedValue(new Error('network down')),
      }),
    })

    await vm.refresh()

    expect(vm.notFound.value).toBe(false)
    expect(vm.loadError.value).toContain('network down')
    expect(vm.client).toBeNull()
  })
})

describe('useClientDashboardViewModel scan aggregation', () => {
  it('sums severityTotals across the loaded scans', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listSecurityScans: vi.fn().mockResolvedValue([
          scan({ scanId: 's1', summary: summary({ critical: 1, high: 2, info: 3 }) }),
          scan({ scanId: 's2', summary: summary({ high: 1, medium: 4, low: 2 }) }),
        ]),
      }),
    })

    await vm.refresh()

    expect(vm.severityTotals.value).toEqual({ critical: 1, high: 3, medium: 4, low: 2, info: 3 })
  })

  it('picks the newest scan by queue time as latestScan and loads its findings', async () => {
    const getScanFindings = vi.fn().mockResolvedValue(findingsPage([finding()]))
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listSecurityScans: vi.fn().mockResolvedValue([
          scan({ scanId: 'older', queuedAt: '2026-10-24T10:00:00Z' }),
          scan({ scanId: 'newer', queuedAt: '2026-10-25T10:00:00Z' }),
        ]),
        getScanFindings,
      }),
    })

    await vm.refresh()

    expect(vm.latestScan.value?.scanId).toBe('newer')
    expect(getScanFindings).toHaveBeenCalledWith('client-1', 'newer', { limit: 25 })
    expect(vm.latestFindings.value).toHaveLength(1)
  })

  it('counts only Open and Acknowledged findings as openFindings', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        getScanFindings: vi.fn().mockResolvedValue(findingsPage([
          finding({ id: 'a', status: 'Open' }),
          finding({ id: 'b', status: 'Acknowledged' }),
          finding({ id: 'c', status: 'Suppressed' }),
          finding({ id: 'd', status: 'Resolved' }),
        ])),
      }),
    })

    await vm.refresh()

    expect(vm.openFindings.value).toBe(2)
  })
})

describe('useClientDashboardViewModel counts', () => {
  it('takes reviewsTotal from the history response total and counts pending fix proposals', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listFixProposals: vi.fn().mockResolvedValue([
          proposal({ id: 'p1', status: 'PendingApproval' }),
          proposal({ id: 'p2', status: 'Approved' }),
          proposal({ id: 'p3', status: 'PendingApproval' }),
          proposal({ id: 'p4', status: 'Rejected' }),
        ]),
      }),
    })

    await vm.refresh()

    expect(vm.reviewsTotal.value).toBe(3)
    expect(vm.pendingFixCount.value).toBe(2)
    expect(vm.fixProposalsUnavailable.value).toBe(false)
  })

  it('reports pendingFixCount null (not zero) when the fix-proposals API is unavailable', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listFixProposals: vi.fn().mockRejectedValue(new FixProposalsUnavailableError()),
      }),
    })

    await vm.refresh()

    expect(vm.pendingFixCount.value).toBeNull()
    expect(vm.fixProposalsUnavailable.value).toBe(true)
    expect(vm.fixProposalsError.value).toBe('')
  })

  it('reports reviewsTotal null when the history response carries no total', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listPullRequestHistory: vi.fn().mockResolvedValue({ items: [reviewGroup()] }),
      }),
    })

    await vm.refresh()

    expect(vm.reviewsTotal.value).toBeNull()
    expect(vm.reviewGroups.value).toHaveLength(1)
  })
})

describe('useClientDashboardViewModel independent section failure', () => {
  it('a failed scans load does not blank the reviews or the header', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listSecurityScans: vi.fn().mockRejectedValue(new Error('scans down')),
      }),
    })

    await vm.refresh()

    expect(vm.scansError.value).toContain('scans down')
    expect(vm.scans.value).toEqual([])
    expect(vm.latestScan.value).toBeNull()
    expect(vm.latestFindings.value).toEqual([])
    expect(vm.client?.displayName).toBe('Acme')
    expect(vm.reviewsTotal.value).toBe(3)
    expect(vm.reviewGroups.value).toHaveLength(1)
    expect(vm.pendingFixCount.value).toBe(1)
    expect(vm.reviewsError.value).toBe('')
  })

  it('a failed reviews load does not blank the scans or the header', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listPullRequestHistory: vi.fn().mockRejectedValue(new Error('history down')),
      }),
    })

    await vm.refresh()

    expect(vm.reviewsError.value).toContain('history down')
    expect(vm.reviewsTotal.value).toBeNull()
    expect(vm.reviewGroups.value).toEqual([])
    expect(vm.scans.value).toHaveLength(1)
    expect(vm.latestFindings.value).toHaveLength(1)
    expect(vm.client?.displayName).toBe('Acme')
    expect(vm.scansError.value).toBe('')
  })

  it('a failed findings load leaves the scans and their totals intact', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listSecurityScans: vi.fn().mockResolvedValue([
          scan({ summary: summary({ critical: 2, high: 1 }) }),
        ]),
        getScanFindings: vi.fn().mockRejectedValue(new Error('findings down')),
      }),
    })

    await vm.refresh()

    expect(vm.findingsError.value).toContain('findings down')
    expect(vm.latestFindings.value).toEqual([])
    expect(vm.severityTotals.value).toEqual({ critical: 2, high: 1, medium: 0, low: 0, info: 0 })
    expect(vm.openFindings.value).toBe(0)
  })
})

describe('useClientDashboardViewModel empty states', () => {
  it('reports honest empties — no invented numbers — when every section is empty', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listSecurityScans: vi.fn().mockResolvedValue([]),
        getScanFindings: vi.fn(),
        listFixProposals: vi.fn().mockResolvedValue([]),
        listPullRequestHistory: vi.fn().mockResolvedValue({ total: 0, items: [] }),
      }),
    })

    await vm.refresh()

    expect(vm.scans.value).toEqual([])
    expect(vm.latestScan.value).toBeNull()
    expect(vm.latestFindings.value).toEqual([])
    expect(vm.severityTotals.value).toEqual({ critical: 0, high: 0, medium: 0, low: 0, info: 0 })
    expect(vm.openFindings.value).toBe(0)
    expect(vm.reviewsTotal.value).toBe(0)
    expect(vm.pendingFixCount.value).toBe(0)
    expect(vm.activity.value).toEqual([])
    expect(vm.scansError.value).toBe('')
    expect(vm.reviewsError.value).toBe('')
    expect(vm.findingsError.value).toBe('')
    expect(vm.fixProposalsError.value).toBe('')
  })
})

describe('useClientDashboardViewModel activity', () => {
  it('merges scans and reviews newest first', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listSecurityScans: vi.fn().mockResolvedValue([
          scan({
            scanId: 'old-scan',
            queuedAt: '2026-10-25T08:00:00Z',
            startedAt: '2026-10-25T08:01:00Z',
            finishedAt: '2026-10-25T08:05:00Z',
          }),
          scan({
            scanId: 'new-scan',
            queuedAt: '2026-10-25T14:00:00Z',
            startedAt: '2026-10-25T14:01:00Z',
            finishedAt: '2026-10-25T14:05:00Z',
          }),
        ]),
        listPullRequestHistory: vi.fn().mockResolvedValue(historyResponse([
          reviewGroup({ latestActivityAt: '2026-10-25T11:00:00Z' }),
        ], 1)),
      }),
    })

    await vm.refresh()

    expect(vm.activity.value.map((item) => item.id)).toEqual([
      'scan-new-scan',
      'review-job-1',
      'scan-old-scan',
    ])
    expect(vm.activity.value.map((item) => item.kind)).toEqual(['scan', 'review', 'scan'])
  })

  it('links review items to the job protocol and scan items to the scans page', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService(),
    })

    await vm.refresh()

    const review = vm.activity.value.find((item) => item.kind === 'review')
    expect(review?.to).toEqual({
      name: 'job-protocol',
      params: { id: 'job-1' },
      query: { clientId: 'client-1' },
    })
    expect(review?.status).toBe('completed')

    const scanItem = vm.activity.value.find((item) => item.kind === 'scan')
    expect(scanItem?.to).toEqual({ name: 'security-scans' })
    expect(scanItem?.status).toBe('Succeeded')
  })

  it('skips a review group with no jobs instead of linking nowhere', async () => {
    const vm = useClientDashboardViewModel('client-1', {
      autoLoad: false,
      dashboardService: happyService({
        listPullRequestHistory: vi.fn().mockResolvedValue(historyResponse([
          reviewGroup({ jobs: [] }),
          reviewGroup({ pullRequestId: 8, latestActivityAt: '2026-10-25T13:00:00Z' }),
        ], 2)),
      }),
    })

    await vm.refresh()

    expect(vm.activity.value.filter((item) => item.kind === 'review')).toHaveLength(1)
    expect(vm.activity.value[0].id).toBe('review-job-1')
  })
})
