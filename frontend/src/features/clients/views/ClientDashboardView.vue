<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<!--
  Per-client dashboard. Composes, in order: header (client + quick counts),
  scans (compact list), reviews (ReviewHistorySection), posture (PostureView
  over the latest scan), fix-queue snapshot, recent activity.

  All data comes from useClientDashboardViewModel; this view renders what it
  reports and never invents a number. The Reviews section is used as-is:
  ReviewHistorySection has no cap prop and already renders its own
  "No reviews yet." empty state.
-->
<template>
  <PageWithSidebar>
    <template #sidebar>
      <AppNavDrawer>
        <RouterLink class="back-link" :to="{ name: 'clients' }">
          <i class="fi fi-rr-arrow-left"></i> Back to clients
        </RouterLink>
        <div v-if="client" class="detail-page-title">
          <h2>{{ client.displayName }}</h2>
          <p class="detail-page-subtitle">Client Dashboard</p>
        </div>
      </AppNavDrawer>
    </template>

    <AppTopBar class="view-header">
      <div>
        <h2 class="view-title">Client Dashboard</h2>
        <p class="view-subtitle">
          Scans, reviews, posture, and fix queue for one client — everything below comes from a real run.
        </p>
      </div>
      <RouterLink class="btn-ghost" :to="{ name: 'clients' }">All clients</RouterLink>
    </AppTopBar>

    <!-- Loading skeleton -->
    <div v-if="loading" class="dashboard-skeleton" aria-label="Loading client dashboard">
      <div class="skeleton-line skeleton-title"></div>
      <div class="skeleton-line"></div>
      <div class="skeleton-line"></div>
      <div class="skeleton-line skeleton-short"></div>
    </div>

    <p v-else-if="notFound" class="empty-state" role="status">Client not found.</p>

    <div v-else-if="loadError" class="error-block" role="alert">
      <p class="error">{{ loadError }}</p>
      <button class="btn-secondary" @click="refresh()">Retry</button>
    </div>

    <div v-else class="client-dashboard">
      <!-- Header -->
      <header class="dashboard-header">
        <div class="dashboard-header-main">
          <h3 class="dashboard-client-name">{{ client?.displayName ?? 'Client' }}</h3>
          <p v-if="tenantDisplayName" class="dashboard-tenant">{{ tenantDisplayName }}</p>
          <p class="dashboard-meta">
            <span
              class="status-chip"
              :class="client?.isActive ? 'status-chip-active' : 'status-chip-inactive'"
            >{{ client?.isActive ? 'Active' : 'Inactive' }}</span>
            <span v-if="client">Created {{ formatDashboardDate(client.createdAt) }}</span>
          </p>
        </div>
        <div class="dashboard-counts">
          <div class="dashboard-count">
            <span class="dashboard-count-num"><CountUp :value="scans.length" /></span>
            <span class="dashboard-count-cap">scans</span>
          </div>
          <div class="dashboard-count">
            <span v-if="reviewsTotal != null" class="dashboard-count-num"><CountUp :value="reviewsTotal" /></span>
            <span v-else class="dashboard-count-num">—</span>
            <span class="dashboard-count-cap">reviews</span>
          </div>
          <div class="dashboard-count">
            <span class="dashboard-count-num"><CountUp :value="openFindings" /></span>
            <span class="dashboard-count-cap">open findings</span>
          </div>
          <div class="dashboard-count">
            <span v-if="pendingFixCount != null" class="dashboard-count-num"><CountUp :value="pendingFixCount" /></span>
            <span v-else class="dashboard-count-num">—</span>
            <span class="dashboard-count-cap">pending fixes</span>
          </div>
        </div>
      </header>

      <!-- Scans -->
      <section class="dashboard-section" aria-label="Scans">
        <h3 class="dashboard-section-title">Scans</h3>
        <ClientDashboardScansSection
          :scans="scans"
          :loading="scansLoading"
          :error="scansError"
        />
      </section>

      <!-- Reviews -->
      <section class="dashboard-section" aria-label="Reviews">
        <h3 class="dashboard-section-title">Reviews</h3>
        <ReviewHistorySection :client-id="clientId" />
      </section>

      <!-- Trigger review -->
      <section class="dashboard-section" aria-label="Trigger review">
        <h3 class="dashboard-section-title">Trigger review</h3>
        <TriggerReviewCard :client-id="clientId" />
      </section>

      <!-- Posture -->
      <section class="dashboard-section" aria-label="Latest scan posture">
        <h3 class="dashboard-section-title">Latest scan posture</h3>
        <PostureView
          v-if="latestScan"
          :scan="latestScan"
          :scans="scans"
          :findings="latestFindings"
          :total-findings="latestScanTotal"
        />
        <p v-else class="empty-state">No scans yet — posture appears after the first scan</p>
      </section>

      <!-- Fix queue snapshot -->
      <section class="dashboard-section" aria-label="Fix queue">
        <h3 class="dashboard-section-title">Fix queue</h3>
        <div class="fix-queue-snapshot">
          <p v-if="fixProposalsUnavailable" class="empty-state">Fix proposals unavailable</p>
          <p v-else-if="fixProposalsError" class="error" role="alert">{{ fixProposalsError }}</p>
          <p v-else class="fix-queue-count">
            <span v-if="pendingFixCount != null" class="fix-queue-num"><CountUp :value="pendingFixCount" /></span>
            <span v-else class="fix-queue-num">—</span>
            {{ pendingFixCount === 1 ? 'fix proposal' : 'fix proposals' }} waiting for a human decision
          </p>
          <RouterLink class="btn-ghost" :to="{ name: 'reviews' }">Open Fix Queue ↗</RouterLink>
        </div>
      </section>

      <!-- Recent activity -->
      <section class="dashboard-section" aria-label="Recent activity">
        <h3 class="dashboard-section-title">Recent activity</h3>
        <ClientDashboardActivitySection :activity="activity" />
      </section>
    </div>
  </PageWithSidebar>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { AppNavDrawer, AppTopBar, PageWithSidebar } from '@/components'
import CountUp from '@/features/security-scans/components/CountUp.vue'
import PostureView from '@/features/security-scans/components/PostureView.vue'
import ReviewHistorySection from '@/features/reviews/components/ReviewHistorySection.vue'
import { summaryTotal } from '@/features/security-scans/scanInsights'
import { useClientDashboardViewModel } from '@/features/clients/view-models/useClientDashboardViewModel'
import ClientDashboardActivitySection from '@/features/clients/components/ClientDashboardActivitySection.vue'
import ClientDashboardScansSection from '@/features/clients/components/ClientDashboardScansSection.vue'
import TriggerReviewCard from '@/features/clients/components/TriggerReviewCard.vue'
import { formatDashboardDate } from '@/features/clients/components/clientDashboardFormatters'

const route = useRoute()
const clientId = computed(() => String(route.params.id ?? ''))
const vm = useClientDashboardViewModel(clientId)

// Destructure the view-model refs so the template auto-unwraps them. `client`
// and `tenantDisplayName` are plain values in the contract, so they are
// wrapped in computeds to keep them reactive.
const client = computed(() => vm.client)
const tenantDisplayName = computed(() => vm.tenantDisplayName)
const {
  loading,
  loadError,
  notFound,
  scans,
  scansLoading,
  scansError,
  latestScan,
  latestFindings,
  openFindings,
  reviewsTotal,
  pendingFixCount,
  fixProposalsError,
  fixProposalsUnavailable,
  activity,
  refresh,
} = vm

const latestScanTotal = computed(() =>
  latestScan.value ? summaryTotal(latestScan.value.summary) : 0,
)
</script>

<style scoped>
.view-header {
  margin-bottom: 1.5rem;
}

.view-title {
  margin: 0;
}

.view-subtitle {
  margin: 0.25rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.9rem;
}

.back-link {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  margin-bottom: 1rem;
  color: var(--color-accent);
  text-decoration: none;
  font-size: 0.9rem;
}

.detail-page-title h2 {
  font-size: 1.25rem;
  margin: 0;
}

.detail-page-subtitle {
  color: var(--color-text-muted);
  font-size: 0.85rem;
  margin: 0.25rem 0 0;
}

.empty-state {
  color: var(--color-text-muted);
  font-style: italic;
}

.error {
  color: var(--color-danger);
}

.error-block {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.75rem;
}

.dashboard-skeleton {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  max-width: 42rem;
}

.skeleton-line {
  height: 1rem;
  border-radius: var(--radius-xs);
  background: rgba(148, 163, 184, 0.18);
  animation: skeleton-pulse 1.4s ease-in-out infinite;
}

.skeleton-title {
  height: 1.6rem;
  width: 55%;
}

.skeleton-short {
  width: 35%;
}

@keyframes skeleton-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.45; }
}

.client-dashboard {
  display: flex;
  flex-direction: column;
  gap: 1.75rem;
  max-width: 72rem;
}

.dashboard-header {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.25rem 1.4rem;
  color: #e2e8f0;
  display: flex;
  flex-wrap: wrap;
  gap: 1.5rem;
  align-items: center;
  justify-content: space-between;
}

.dashboard-client-name {
  margin: 0;
  font-size: 1.35rem;
}

.dashboard-tenant {
  margin: 0.25rem 0 0;
  color: #94a3b8;
  font-size: 0.85rem;
}

.dashboard-meta {
  margin: 0.6rem 0 0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.75rem;
  font-size: 0.85rem;
  color: #94a3b8;
}

.status-chip {
  display: inline-block;
  padding: 0.15rem 0.6rem;
  border-radius: var(--radius-pill);
  font-size: 0.75rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.status-chip-active {
  background: rgba(16, 185, 129, 0.15);
  color: #34d399;
}

.status-chip-inactive {
  background: rgba(148, 163, 184, 0.15);
  color: #94a3b8;
}

.dashboard-counts {
  display: grid;
  grid-template-columns: repeat(4, minmax(90px, 1fr));
  gap: 1rem;
  flex: 1;
  min-width: 260px;
}

@media (max-width: 640px) {
  .dashboard-counts {
    grid-template-columns: repeat(2, 1fr);
  }
}

.dashboard-count {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  border-left: 2px solid rgba(245, 158, 11, 0.45);
  padding-left: 0.7rem;
}

.dashboard-count-num {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 1.55rem;
  font-weight: 700;
  color: #7dd3fc;
  line-height: 1.1;
}

.dashboard-count-cap {
  font-size: 0.74rem;
  color: #94a3b8;
}

.dashboard-section {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

.dashboard-section-title {
  margin: 0;
  font-size: 1.05rem;
}

.fix-queue-snapshot {
  background: #0a1628;
  border: 1px solid rgba(56, 189, 248, 0.18);
  border-radius: 14px;
  padding: 1.1rem 1.4rem;
  color: #e2e8f0;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 1rem;
  justify-content: space-between;
}

.fix-queue-snapshot .empty-state {
  margin: 0;
}

.fix-queue-count {
  margin: 0;
  font-size: 0.95rem;
}

.fix-queue-num {
  font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
  font-size: 1.4rem;
  font-weight: 700;
  color: #fbbf24;
  margin-right: 0.35rem;
}
</style>
