<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  Job protocol view wrapper.

  Renders the protocol trace from @/features/job-protocol, then the review depth panel below it:
  files/tokens/cost, passes, the verification funnel, and the published findings with the lens
  filter and per-finding Draft fix / Dismiss actions. The router points the `job-protocol`
  route at this file; the same depth panel is also live on the PR review page (per-job detail)
  and on each review history row (Depth toggle).
-->

<template>
    <div class="job-protocol-page">
        <FeatureJobProtocolView />
        <div v-if="jobId" class="protocol-depth-wrap">
            <ReviewDepthPanel :job-id="jobId" :client-id="clientId" :can-dismiss="canDismiss" />
        </div>
    </div>
</template>

<script lang="ts" setup>
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import FeatureJobProtocolView from '@/features/job-protocol/views/JobProtocolView.vue'
import ReviewDepthPanel from '@/features/reviews/components/ReviewDepthPanel.vue'
import { useSession } from '@/composables/useSession'
import { RoleLevel } from '@/composables/roles'

const route = useRoute()
const { hasClientRole } = useSession()

const jobId = computed(() => route.params.id as string | undefined)
const clientId = computed(() => route.query.clientId as string | undefined)
const canDismiss = computed(
    () => typeof clientId.value === 'string' && clientId.value.length > 0 && hasClientRole(clientId.value, RoleLevel.Administrator),
)
</script>

<style scoped>
.job-protocol-page {
    display: flex;
    flex-direction: column;
}

.protocol-depth-wrap {
    padding: 0 0 1.5rem;
}
</style>
