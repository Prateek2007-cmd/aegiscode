<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->
<template>
  <section
    class="measurement-coverage"
    :aria-label="`${label} coverage`"
  >
    <div class="measurement-coverage__heading">
      <div>
        <strong>{{ label }}</strong>
        <span class="measurement-coverage__window">
          {{ point?.windowFrom ?? point?.date }} → {{ point?.windowTo ?? point?.date }}
        </span>
      </div>
      <button
        v-if="(coverage?.exclusionIds?.length ?? 0) > 0"
        type="button"
        class="performance-action"
        :disabled="disabled"
        @click="$emit('exclusions', $event)"
      >
        View exclusions ({{ coverage?.excludedPullRequests ?? 0 }})
      </button>
    </div>
    <template v-if="coverage">
      <p class="measurement-coverage__counts">
        <strong>
          {{ coverage.coveredPullRequests ?? 0 }} of {{ coverage.candidatePullRequests ?? 0 }} PRs covered
        </strong>
        <span>
          · {{ coverage.percentage == null ? 'Percentage unavailable' : `${coverage.percentage.toFixed(1)}%` }}
          · {{ coverage.excludedPullRequests ?? 0 }} excluded
        </span>
      </p>
      <p>{{ status }}</p>
      <p v-if="policy" class="measurement-coverage__note">
        Advisory minimum: {{ policy.minimumCoveragePercentage }}% and {{ policy.minimumCoveredPullRequests }}
        covered PR{{ policy.minimumCoveredPullRequests === 1 ? '' : 's' }}.
      </p>
      <p v-if="metric !== 'precision' && !coverage.meetsMinimum && hasScore" class="measurement-coverage__note">
        Coverage is below the advisory minimum. Scores use the covered PRs.
      </p>
      <p class="measurement-coverage__note">
        The Precision metric uses all supported published outcomes. F1 uses precision and recall from the same covered PRs.
        Coverage does not establish that this subset represents all PRs.
      </p>
      <p v-if="modelEvidence" class="measurement-coverage__note">
        Model scores use verified applicable review opportunities and human concerns. Models can cover different workloads.
        Selected-model Combined counts each missed concern and source PR once.
      </p>
    </template>
    <p v-else>Coverage details were not captured for this measurement.</p>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { PerformancePoint, PerformanceCoveragePolicy } from '@/services/reviewerPerformanceService'
import { metricLabel, metricRange, pointAvailability, type PerformanceMetric } from '../reviewerPerformanceRanges'

const props = defineProps<{
  point?: PerformancePoint
  policy?: PerformanceCoveragePolicy
  label: string
  metric: PerformanceMetric
  disabled?: boolean
  modelEvidence?: boolean
}>()
defineEmits<{ exclusions: [event: MouseEvent] }>()
const coverage = computed(() => props.point?.coverage)
const hasScore = computed(() => props.point && metricRange(props.point, props.metric)?.median != null)
const status = computed(() => {
  if (!props.point) return 'No measurement is available.'
  if (hasScore.value) return `${metricLabel(props.metric)} is available for this measurement.`
  if (props.metric !== 'precision' && coverage.value?.recallSupported === false) {
    return `${metricLabel(props.metric)} unavailable: compatible model attribution is not retained.`
  }
  if (pointAvailability(props.point, props.metric) === 'inactive') {
    return props.metric === 'f1'
      ? 'F1 is unavailable because covered PRs have no scored outcomes or settled human misses in this period.'
      : props.metric === 'recall'
        ? 'Observed recall is unavailable because covered PRs have no positive scored outcomes or settled human misses in this period.'
        : 'Precision is unavailable because no published outcomes have a positive or negative scoring contribution in this period.'
  }
  if (props.metric !== 'precision' && coverage.value?.coveredPullRequests === 0) {
    return `${metricLabel(props.metric)} unavailable: no PRs have complete compatible evidence in this period.`
  }
  return `${metricLabel(props.metric)} unavailable. Inspect the evidence limitations.`
})
</script>

<style scoped>
.measurement-coverage {
  margin-top: 1rem;
  padding: 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: var(--color-surface-raised);
  font-size: 0.8rem;
}

.measurement-coverage__heading {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  gap: 0.75rem;
}

.measurement-coverage__heading > div {
  display: grid;
  gap: 0.25rem;
}

.measurement-coverage__window, .measurement-coverage__note {
  color: var(--color-text-muted);
}

.measurement-coverage p {
  margin: 0.5rem 0 0;
  line-height: 1.6;
}

.measurement-coverage__counts {
  font-variant-numeric: tabular-nums;
}

.measurement-coverage button {
  min-height: 44px;
}
</style>
