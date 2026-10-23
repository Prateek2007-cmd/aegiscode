<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <PageWithSidebar>
        <template #sidebar>
            <AppNavDrawer>
                <div class="sidebar-nav">
                    <div class="sidebar-nav-group">
                        <h4>Reviews</h4>
                        <button class="sidebar-nav-link active">
                            <i class="fi fi-rr-time-past"></i> Global History
                        </button>
                    </div>
                </div>
            </AppNavDrawer>
        </template>

        <AppTopBar class="view-header">
            <div>
                <h2 class="view-title">Review History</h2>
                <p class="view-subtitle">Automated AI code reviews across repositories, with per-PR findings and full history.</p>
            </div>
            <button class="btn-primary" @click="handleRefresh" :disabled="isRefreshing" title="Refresh review history">
                <i class="fi fi-rr-refresh"></i>
                {{ isRefreshing ? 'Refreshing…' : 'Refresh' }}
            </button>
        </AppTopBar>
        <SecurityPostureBanner />
        <ReviewHistorySection ref="historySection" />
    </PageWithSidebar>
</template>

<script lang="ts" setup>
import { ref } from 'vue'
import { AppNavDrawer, AppTopBar, PageWithSidebar } from '@/components'
import ReviewHistorySection from '@/features/reviews/components/ReviewHistorySection.vue'
import SecurityPostureBanner from '@/features/reviews/components/SecurityPostureBanner.vue'

const historySection = ref<InstanceType<typeof ReviewHistorySection>>()
const isRefreshing = ref(false)

async function handleRefresh() {
    isRefreshing.value = true
    try {
        await historySection.value?.refresh()
    } finally {
        isRefreshing.value = false
    }
}
</script>

<style scoped>
.view-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 1.5rem;
}

.view-title {
    margin: 0;
    font-size: 1.6rem;
    font-weight: 700;
    color: var(--color-text);
}

.view-subtitle {
    margin: 0.35rem 0 0;
    font-size: 0.88rem;
    color: var(--color-text-muted);
}

.btn-primary {
    display: inline-flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}
</style>
