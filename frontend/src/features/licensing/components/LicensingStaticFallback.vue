<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<script setup lang="ts">
/**
 * Static free-tier licensing state, shown only when the live licensing summary could not be read
 * (GET /admin/licensing failed, e.g. the licensing module is not registered because no database
 * connection string is configured, or the read threw). Nothing here is a license on file: it is the
 * built-in community state from LICENSING.md plus the commercial-only capability catalog, so the page
 * never renders empty. If a commercial license is installed, it is NOT reflected here — the live
 * read must succeed for that, and the note below says so.
 */
const capabilities = [
  {
    key: 'sso-authentication',
    displayName: 'Single sign-on',
    reason: 'A commercial license is required to use single sign-on, including in self-hosted deployments.',
  },
  {
    key: 'parallel-review-execution',
    displayName: 'Parallel review execution',
    reason: 'A commercial license is required to run more than one active PR review at a time, including in self-hosted deployments.',
  },
  {
    key: 'distributed-execution',
    displayName: 'Distributed review execution',
    reason: 'A commercial license is required to run reviews on registered runners, including in self-hosted deployments.',
  },
  {
    key: 'multiple-scm-providers',
    displayName: 'Multiple SCM providers',
    reason: 'A commercial license is required to configure more than one SCM provider, including in self-hosted deployments.',
  },
  {
    key: 'crawl-configs',
    displayName: 'Crawl configurations',
    reason: 'A commercial license is required to manage guided crawl configurations and discovery, including in self-hosted deployments.',
  },
  {
    key: 'mention-answering',
    displayName: 'Mention answering',
    reason: 'A commercial license is required to answer @-mentions in pull request comments, including in self-hosted deployments.',
  },
  {
    key: 'budgeting',
    displayName: 'Budgeting',
    reason: 'A commercial license is required to set USD spend budgets and to view spend against them, including in self-hosted deployments.',
  },
  {
    key: 'code-insights',
    displayName: 'Code Insights',
    reason: 'A commercial license is required to collect and view code-quality insights, including in self-hosted deployments.',
  },
  {
    key: 'multi-tenancy',
    displayName: 'Multi-tenancy',
    reason: 'A commercial license is required to use more than the built-in System tenant, including in self-hosted deployments.',
  },
]
</script>

<template>
  <section class="section-card licensing-static-fallback" data-testid="licensing-static-fallback">
    <div class="section-card-header">
      <div>
        <h3>Community edition — Elastic License 2.0</h3>
        <p class="licensing-subtitle">
          The live licensing state could not be read from the API, so this page shows the built-in
          free-tier state instead of an empty panel. A commercial license on file is not reflected
          here — fix the licensing read and reload to see it.
        </p>
      </div>
      <span class="chip chip-muted" data-testid="licensing-static-edition-chip">Community active</span>
    </div>

    <div class="section-card-body">
      <p class="licensing-static-note">
        This installation runs under the Elastic License 2.0 (see LICENSE and LICENSING.md). The
        capabilities below are commercial-only and are not active on the free tier.
      </p>

      <div class="licensing-capability-grid">
        <article
          v-for="capability in capabilities"
          :key="capability.key"
          class="licensing-capability-card"
          :data-testid="`license-capability-${capability.key}`"
        >
          <div class="licensing-capability-header">
            <h4>{{ capability.displayName }}</h4>
            <span class="chip chip-sm chip-muted">Not active</span>
          </div>
          <p class="licensing-capability-reason">{{ capability.reason }}</p>
        </article>
      </div>
    </div>
  </section>
</template>

<style scoped>
.licensing-subtitle {
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}

.licensing-static-fallback .section-card-body {
  padding: 1.25rem;
}

.licensing-static-note {
  color: var(--color-text-muted);
  margin: 0 0 1rem;
}
</style>
