// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Per-client dashboard view-model.
 *
 * Loads the client header, its recent security scans (and the latest scan's
 * findings), its review history and its pending fix proposals. Every section
 * loads and fails independently: a failed scans load leaves the reviews
 * untouched and vice versa, and every section reports its own error string
 * instead of blanking the page. Nothing here invents numbers — an empty
 * section is an empty array or a null, never a zero dressed up as a count
 * the server never reported.
 */
import { computed, onMounted, ref, toValue, type ComputedRef, type MaybeRefOrGetter, type Ref } from 'vue'
import type { RouteLocationRaw } from 'vue-router'
import { createAdminClient } from '@/services/api'
import type { ClientDetailDto } from './useClientDetailViewModel'
import type { ClientListItem } from './useClientsViewModel'
import {
  getScanFindings,
  listSecurityScans,
} from '@/features/security-scans/securityScansService'
import type {
  PagedSecurityFindings,
  SecurityFinding,
  SecurityScan,
  SecurityScanSummary,
} from '@/features/security-scans/types'
import { summaryTotal } from '@/features/security-scans/scanInsights'
import {
  isFixProposalsUnavailable,
  listFixProposals,
  type FixProposal,
} from '@/services/fixProposalsService'
import {
  listPullRequestHistory,
  type ListPullRequestHistoryParams,
  type PullRequestHistoryItem,
  type PullRequestHistoryResponse,
} from '@/services/jobsService'

export interface ClientDashboardActivityItem {
  id: string
  kind: 'scan' | 'review'
  title: string
  detail: string
  at: string
  status: string
  to: RouteLocationRaw
}

export interface ClientDashboardData {
  client: ClientDetailDto | null
  tenantDisplayName: string | null
  loading: Ref<boolean>
  loadError: Ref<string>
  notFound: Ref<boolean>
  scans: Ref<SecurityScan[]>
  scansLoading: Ref<boolean>
  scansError: Ref<string>
  latestScan: Ref<SecurityScan | null>
  latestFindings: Ref<SecurityFinding[]>
  severityTotals: Ref<SecurityScanSummary>
  /** Count of Open or Acknowledged findings among the latest scan's loaded findings. */
  openFindings: Ref<number>
  reviewsTotal: Ref<number | null>
  pendingFixCount: Ref<number | null>
  activity: Ref<ClientDashboardActivityItem[]>
  refresh: () => Promise<void>
  // Added fields (the contract above is untouched):
  /** Per-section error for the review-history load; '' when it loaded or was never attempted. */
  reviewsError: Ref<string>
  /** Per-section error for the latest scan's findings load; '' when it loaded or there is no scan. */
  findingsError: Ref<string>
  /** Per-section error for the fix-proposals load; '' when it loaded or the API is unavailable. */
  fixProposalsError: Ref<string>
  /** True when the fix-proposals API answered 404 — the API update is not deployed yet, not an empty queue. */
  fixProposalsUnavailable: Ref<boolean>
  /** The review-history groups behind the activity feed, most recently active first. */
  reviewGroups: Ref<PullRequestHistoryItem[]>
}

/** Injectable seams so tests can stub every source without touching the network. */
export interface ClientDashboardService {
  getClient: (clientId: string) => Promise<{ data: ClientDetailDto | null; response?: Response | { status?: number; ok?: boolean } }>
  listClients: () => Promise<ClientListItem[]>
  listSecurityScans: (clientId: string, limit: number, offset: number) => Promise<SecurityScan[]>
  getScanFindings: (clientId: string, scanId: string, query: { limit?: number }) => Promise<PagedSecurityFindings>
  listFixProposals: (clientId: string) => Promise<FixProposal[]>
  listPullRequestHistory: (params: ListPullRequestHistoryParams) => Promise<PullRequestHistoryResponse>
}

export interface UseClientDashboardViewModelOptions {
  dashboardService?: Partial<ClientDashboardService>
  autoLoad?: boolean
}

const SCANS_LIMIT = 10
const FINDINGS_LIMIT = 25
const REVIEWS_LIMIT = 10

const EMPTY_SUMMARY: SecurityScanSummary = { critical: 0, high: 0, medium: 0, low: 0, info: 0 }

async function defaultGetClient(clientId: string) {
  return createAdminClient().GET('/clients/{clientId}', {
    params: { path: { clientId } },
  })
}

async function defaultListClients(): Promise<ClientListItem[]> {
  const { data, response } = await createAdminClient().GET('/clients', {})
  if (!response.ok) {
    throw new Error('Failed to load clients.')
  }
  return (data as ClientListItem[]) ?? []
}

async function defaultListFixProposals(clientId: string) {
  return listFixProposals(clientId)
}

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message.trim().length > 0 ? error.message : fallback
}

/** The scan's most recent activity timestamp: finish, else start, else queue time. */
function scanActivityAt(scan: SecurityScan): string {
  return scan.finishedAt ?? scan.startedAt ?? scan.queuedAt ?? ''
}

/** Picks the newest scan by queue time; the API does not promise an order. */
function pickLatestScan(scans: SecurityScan[]): SecurityScan | null {
  if (scans.length === 0) return null
  return scans.reduce((latest, scan) =>
    (scan.queuedAt ?? '') >= (latest.queuedAt ?? '') ? scan : latest,
  )
}

function toActivityTime(value: string | undefined | null): number {
  if (!value) return Number.NEGATIVE_INFINITY
  const parsed = Date.parse(value)
  return Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed
}

export function useClientDashboardViewModel(
  clientId: MaybeRefOrGetter<string>,
  options: UseClientDashboardViewModelOptions = {},
): ClientDashboardData {
  const getClientFn = options.dashboardService?.getClient ?? defaultGetClient
  const listClientsFn = options.dashboardService?.listClients ?? defaultListClients
  const listSecurityScansFn = options.dashboardService?.listSecurityScans ?? listSecurityScans
  const getScanFindingsFn = options.dashboardService?.getScanFindings ?? getScanFindings
  const listFixProposalsFn = options.dashboardService?.listFixProposals ?? defaultListFixProposals
  const listPullRequestHistoryFn = options.dashboardService?.listPullRequestHistory ?? listPullRequestHistory
  const autoLoad = options.autoLoad ?? true

  const client = ref<ClientDetailDto | null>(null)
  const tenantDisplayName = ref<string | null>(null)
  const loading = ref(false)
  const loadError = ref('')
  const notFound = ref(false)
  const scans = ref<SecurityScan[]>([])
  const scansLoading = ref(false)
  const scansError = ref('')
  const latestFindings = ref<SecurityFinding[]>([])
  const findingsError = ref('')
  const reviewsError = ref('')
  const fixProposalsError = ref('')
  const fixProposalsUnavailable = ref(false)
  const reviewsTotal = ref<number | null>(null)
  const pendingFixCount = ref<number | null>(null)
  const reviewGroups = ref<PullRequestHistoryItem[]>([])

  const latestScan: ComputedRef<SecurityScan | null> = computed(() => pickLatestScan(scans.value))

  const severityTotals: ComputedRef<SecurityScanSummary> = computed(() => {
    const totals: SecurityScanSummary = { ...EMPTY_SUMMARY }
    for (const scan of scans.value) {
      const summary = scan.summary ?? EMPTY_SUMMARY
      totals.critical += summary.critical ?? 0
      totals.high += summary.high ?? 0
      totals.medium += summary.medium ?? 0
      totals.low += summary.low ?? 0
      totals.info += summary.info ?? 0
    }
    return totals
  })

  /** Open or Acknowledged findings among the latest scan's loaded findings. */
  const openFindings: ComputedRef<number> = computed(() =>
    latestFindings.value.filter((f) => f.status === 'Open' || f.status === 'Acknowledged').length,
  )

  const activity: ComputedRef<ClientDashboardActivityItem[]> = computed(() => {
    const id = toValue(clientId)
    const items: ClientDashboardActivityItem[] = []

    for (const scan of scans.value) {
      items.push({
        id: `scan-${scan.scanId}`,
        kind: 'scan',
        title: `Security scan: ${scan.repositoryUrl}`,
        detail: `${scan.status} · ${summaryTotal(scan.summary ?? EMPTY_SUMMARY)} findings`,
        at: scanActivityAt(scan),
        status: scan.status,
        to: { name: 'security-scans' },
      })
    }

    for (const group of reviewGroups.value) {
      // jobs arrive running-first, then most recent first; the first job is the group's latest run.
      const job = group.jobs?.[0]
      // Without a job there is nothing to link the activity item to, so the group is skipped
      // rather than shown with a dead link.
      if (!job?.id) continue
      const repo = group.prRepositoryName ?? group.repositoryId ?? 'unknown repository'
      const title = group.prTitle
        ? `Review: ${group.prTitle}`
        : `Review: PR #${group.pullRequestId ?? ''}`.trim()
      items.push({
        id: `review-${job.id}`,
        kind: 'review',
        title,
        detail: `${repo} · ${job.status ?? 'unknown'}`,
        at: group.latestActivityAt ?? job.completedAt ?? job.submittedAt ?? '',
        status: job.status ?? 'unknown',
        to: { name: 'job-protocol', params: { id: job.id }, query: { clientId: id } },
      })
    }

    // Newest first; items with no parseable time sort last instead of first.
    return items.sort((a, b) => toActivityTime(b.at) - toActivityTime(a.at))
  })

  async function loadClientSection(id: string): Promise<void> {
    loadError.value = ''
    notFound.value = false
    try {
      const { data, response } = await getClientFn(id)
      if (response && (response as Response).status === 404) {
        notFound.value = true
        client.value = null
        return
      }
      if (!data) {
        loadError.value = 'Failed to load client.'
        return
      }
      client.value = data as ClientDetailDto
    } catch (error) {
      loadError.value = errorMessage(error, 'Failed to load client.')
    }
  }

  async function loadTenantSection(id: string): Promise<void> {
    // The tenant name is a header nicety from the clients list; a failed list load
    // leaves it null rather than failing the header.
    try {
      const clients = await listClientsFn()
      tenantDisplayName.value = clients.find((c) => c.id === id)?.tenantDisplayName ?? null
    } catch {
      tenantDisplayName.value = null
    }
  }

  async function loadScansSection(id: string): Promise<void> {
    scansLoading.value = true
    scansError.value = ''
    try {
      scans.value = await listSecurityScansFn(id, SCANS_LIMIT, 0)
    } catch (error) {
      scans.value = []
      scansError.value = errorMessage(error, 'Failed to load security scans.')
    } finally {
      scansLoading.value = false
    }
  }

  async function loadFindingsSection(id: string): Promise<void> {
    findingsError.value = ''
    const scan = latestScan.value
    if (!scan) {
      latestFindings.value = []
      return
    }
    try {
      const page = await getScanFindingsFn(id, scan.scanId, { limit: FINDINGS_LIMIT })
      latestFindings.value = page?.items ?? []
    } catch (error) {
      latestFindings.value = []
      findingsError.value = errorMessage(error, 'Failed to load findings for the latest scan.')
    }
  }

  async function loadReviewsSection(id: string): Promise<void> {
    reviewsError.value = ''
    try {
      const response = await listPullRequestHistoryFn({ clientId: id, limit: REVIEWS_LIMIT, offset: 0 })
      reviewGroups.value = response?.items ?? []
      reviewsTotal.value = typeof response?.total === 'number' ? response.total : null
    } catch (error) {
      reviewGroups.value = []
      reviewsTotal.value = null
      reviewsError.value = errorMessage(error, 'Failed to load review history.')
    }
  }

  async function loadFixProposalsSection(id: string): Promise<void> {
    fixProposalsError.value = ''
    fixProposalsUnavailable.value = false
    try {
      const proposals = await listFixProposalsFn(id)
      pendingFixCount.value = proposals
        .filter((p) => p.status === 'PendingApproval').length
    } catch (error) {
      pendingFixCount.value = null
      if (isFixProposalsUnavailable(error)) {
        // The API update is not deployed yet: not an error state, and not an empty queue.
        fixProposalsUnavailable.value = true
      } else {
        fixProposalsError.value = errorMessage(error, 'Failed to load fix proposals.')
      }
    }
  }

  async function refresh(): Promise<void> {
    const id = toValue(clientId)
    loading.value = true
    try {
      // Each section fails independently: Promise.allSettled would still run them all, but
      // awaiting them as one group keeps the intent explicit — every loader catches its own
      // failure and records its own error string, so one rejection never blanks another section.
      // Findings depend on the scans result, so they load after the scans section settles.
      await Promise.all([
        loadClientSection(id),
        loadTenantSection(id),
        loadScansSection(id),
        loadReviewsSection(id),
        loadFixProposalsSection(id),
      ])
      await loadFindingsSection(id)
    } finally {
      loading.value = false
    }
  }

  if (autoLoad) {
    onMounted(refresh)
  }

  // client and tenantDisplayName are plain values in the contract, so they are exposed
  // through getters over the backing refs: the view reads a live value, not a snapshot
  // taken before the load finished.
  return {
    get client() { return client.value },
    get tenantDisplayName() { return tenantDisplayName.value },
    loading,
    loadError,
    notFound,
    scans,
    scansLoading,
    scansError,
    latestScan,
    latestFindings,
    severityTotals,
    openFindings,
    reviewsTotal,
    pendingFixCount,
    activity,
    refresh,
    reviewsError,
    findingsError,
    fixProposalsError,
    fixProposalsUnavailable,
    reviewGroups,
  }
}
