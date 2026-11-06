<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<!--
  AegisCode security capability status banner.

  Honesty contract for this component (2026-10-23 audit):
  - It must NEVER display fabricated security metrics (no invented scores,
    grades, leak counts, or CVE counts).
  - Every status shown here must reflect a real, verifiable product capability.
  - Deterministic scanners (secret detection, dependency/CVE matching, SBOM)
    are NOT implemented in this build, so they are labelled "Planned" and
    linked to the public roadmap — never presented as active.
-->
<template>
  <section class="security-status-banner" aria-label="Security capability status">
    <div class="status-main">
      <div class="status-cluster">
        <div class="status-icon-wrap" aria-hidden="true">
          <i class="fi fi-rr-shield-check"></i>
        </div>
        <div class="status-info">
          <div class="status-title-row">
            <h3 class="status-title">Security capability status</h3>
            <span class="status-badge status-badge-partial">
              <span class="status-dot"></span>
              Partial coverage
            </span>
          </div>
          <p class="status-subtitle">
            AI-powered code review is active and its findings appear in the history below.
            Deterministic security scanners are not yet wired into this deployment —
            nothing on this page implies a scan that did not run.
          </p>
        </div>
      </div>

      <div class="capabilities-grid">
        <div class="capability-card capability-live">
          <div class="capability-icon-box capability-icon-review">
            <i class="fi fi-rr-sparkles"></i>
          </div>
          <div class="capability-content">
            <div class="capability-metric">Active</div>
            <div class="capability-label">AI Code Review</div>
          </div>
        </div>

        <div class="capability-card">
          <div class="capability-icon-box capability-icon-secrets">
            <i class="fi fi-rr-key"></i>
          </div>
          <div class="capability-content">
            <div class="capability-metric capability-planned">Planned</div>
            <div class="capability-label">Secret Scanning</div>
          </div>
        </div>

        <div class="capability-card">
          <div class="capability-icon-box capability-icon-sbom">
            <i class="fi fi-rr-cube"></i>
          </div>
          <div class="capability-content">
            <div class="capability-metric capability-planned">Planned</div>
            <div class="capability-label">Dependency &amp; CVE Scanning</div>
          </div>
        </div>

        <div class="capability-card">
          <div class="capability-icon-box capability-icon-remediation">
            <i class="fi fi-rr-document"></i>
          </div>
          <div class="capability-content">
            <div class="capability-metric capability-planned">Planned</div>
            <div class="capability-label">SBOM Generation</div>
          </div>
        </div>
      </div>

      <a class="roadmap-link" href="https://github.com/Prateek2007-cmd/aegiscode/blob/main/docs/SECURITY_ROADMAP.md" target="_blank" rel="noopener">
        <i class="fi fi-rr-map-marker"></i>
        Read the security roadmap
      </a>
    </div>
  </section>
</template>

<script setup lang="ts">
// AegisCode security capability status — honest, data-backed only.
// See the honesty contract in the header comment before changing copy here.
</script>

<style scoped>
.security-status-banner {
  background: var(--color-surface);
  border: 1px solid var(--color-border-hover);
  border-radius: var(--radius-xl);
  padding: 1.25rem 1.5rem;
  margin-bottom: 2rem;
  box-shadow: 0 8px 32px -8px rgba(0, 0, 0, 0.5), inset 0 1px 0 var(--surface-hover);
}

.status-main {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1.5rem;
  flex-wrap: wrap;
}

.status-cluster {
  display: flex;
  align-items: center;
  gap: 1.25rem;
  min-width: 280px;
  flex: 1;
}

.status-icon-wrap {
  width: 58px;
  height: 58px;
  flex-shrink: 0;
  border-radius: 16px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 1.5rem;
  color: var(--color-accent);
  background: var(--color-border);
  border: 1px solid var(--color-border);
}

.status-info {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.status-title-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.status-title {
  margin: 0;
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--color-text, var(--color-text));
  letter-spacing: -0.01em;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.2rem 0.55rem;
  border-radius: 9999px;
  font-size: 0.72rem;
  font-weight: 600;
}

.status-badge-partial {
  background: var(--color-warning-soft);
  border: 1px solid var(--color-warning);
  color: var(--color-warning);
}

.status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background-color: var(--color-warning);
}

.status-subtitle {
  margin: 0;
  font-size: 0.82rem;
  color: var(--color-text-muted, var(--color-text-muted));
  max-width: 60ch;
  line-height: 1.4;
}

.capabilities-grid {
  display: flex;
  align-items: center;
  gap: 0.85rem;
  flex-wrap: wrap;
}

.capability-card {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.6rem 0.85rem;
  background: var(--surface-subtle);
  border: 1px solid var(--color-muted-soft);
  border-radius: 10px;
  transition: all 0.2s ease;
}

.capability-card:hover {
  background: var(--color-border);
  border-color: var(--color-border-hover);
  transform: translateY(-1px);
}

.capability-live {
  border-color: var(--color-success);
}

.capability-icon-box {
  width: 32px;
  height: 32px;
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 0.95rem;
}

.capability-icon-review {
  background: var(--color-success-soft);
  color: var(--color-success);
}

.capability-icon-secrets {
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

.capability-icon-sbom {
  background: var(--color-suggestion-soft);
  color: var(--color-suggestion);
}

.capability-icon-remediation {
  background: var(--color-border);
  color: var(--color-accent);
}

.capability-content {
  display: flex;
  flex-direction: column;
}

.capability-metric {
  font-size: 0.84rem;
  font-weight: 700;
  color: var(--color-text, var(--color-text));
  line-height: 1.2;
}

.capability-planned {
  color: var(--color-text-muted, var(--color-text-muted));
  font-weight: 600;
}

.capability-label {
  font-size: 0.68rem;
  color: var(--color-text-muted, var(--color-text-muted));
  font-weight: 500;
}

.roadmap-link {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--color-accent);
  text-decoration: none;
  white-space: nowrap;
}

.roadmap-link:hover {
  text-decoration: underline;
}
</style>
