<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<!--
  Recent activity for the per-client dashboard. The view model already ordered
  the items newest first; this section only renders them.
-->
<template>
  <div class="dashboard-activity">
    <p v-if="activity.length === 0" class="empty-state">No activity yet</p>
    <ul v-else class="activity-rows">
      <li v-for="item in activity" :key="item.id" class="activity-row-wrap">
        <RouterLink :to="item.to" class="activity-row">
          <span class="activity-kind" :title="activityKindLabel(item.kind)">
            <i :class="activityKindIcon(item.kind)"></i>
            {{ activityKindLabel(item.kind) }}
          </span>
          <span class="activity-main">
            <span class="activity-title">{{ item.title }}</span>
            <span class="activity-detail">{{ item.detail }}</span>
          </span>
          <span class="activity-date">{{ formatDashboardDate(item.at) }}</span>
          <span class="activity-status">{{ item.status }}</span>
        </RouterLink>
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { RouterLink } from 'vue-router'
import type { ClientDashboardActivityItem } from '@/features/clients/view-models/useClientDashboardViewModel'
import {
  activityKindIcon,
  activityKindLabel,
  formatDashboardDate,
} from './clientDashboardFormatters'

defineProps<{
  activity: ClientDashboardActivityItem[]
}>()
</script>

<style scoped>
.dashboard-activity {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-xl);
  padding: 1.1rem 1.4rem;
  color: var(--color-text);
}

.empty-state {
  color: var(--color-text-muted);
  font-style: italic;
  margin: 0;
}

.activity-rows {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.activity-row-wrap {
  border-bottom: 1px solid var(--color-muted-soft);
}

.activity-row-wrap:last-child {
  border-bottom: none;
}

.activity-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.5rem 1rem;
  padding: 0.7rem 0.25rem;
  color: var(--color-text);
  text-decoration: none;
  transition: background 0.15s ease;
}

.activity-row:hover {
  background: var(--color-border);
}

.activity-kind {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-accent);
  flex: 0 0 5.5rem;
}

.activity-main {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  min-width: 0;
  flex: 1 1 16rem;
}

.activity-title {
  font-weight: 600;
  font-size: 0.9rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.activity-detail {
  font-size: 0.82rem;
  color: var(--color-text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.activity-date {
  font-size: 0.82rem;
  color: var(--color-text-muted);
  white-space: nowrap;
}

.activity-status {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--color-warning);
}
</style>
