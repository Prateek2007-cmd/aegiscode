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
  background: linear-gradient(135deg, rgba(15, 23, 42, 0.85) 0%, rgba(17, 24, 39, 0.95) 100%);
  border: 1px solid rgba(56, 189, 248, 0.2);
  border-radius: 14px;
  padding: 1.25rem 1.5rem;
  margin-bottom: 2rem;
  box-shadow: 0 8px 32px -8px rgba(0, 0, 0, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.05);
  backdrop-filter: blur(12px);
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
  color: #38bdf8;
  background: rgba(56, 189, 248, 0.12);
  border: 1px solid rgba(56, 189, 248, 0.3);
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
  color: var(--color-text, #f1f5f9);
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
  background: rgba(245, 158, 11, 0.14);
  border: 1px solid rgba(245, 158, 11, 0.3);
  color: #fbbf24;
}

.status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background-color: #f59e0b;
  box-shadow: 0 0 8px #f59e0b;
}

.status-subtitle {
  margin: 0;
  font-size: 0.82rem;
  color: var(--color-text-muted, #94a3b8);
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
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 10px;
  transition: all 0.2s ease;
}

.capability-card:hover {
  background: rgba(56, 189, 248, 0.06);
  border-color: rgba(56, 189, 248, 0.25);
  transform: translateY(-1px);
}

.capability-live {
  border-color: rgba(16, 185, 129, 0.35);
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
  background: rgba(16, 185, 129, 0.15);
  color: #34d399;
}

.capability-icon-secrets {
  background: rgba(244, 63, 94, 0.15);
  color: #fb7185;
}

.capability-icon-sbom {
  background: rgba(168, 85, 247, 0.15);
  color: #c084fc;
}

.capability-icon-remediation {
  background: rgba(56, 189, 248, 0.15);
  color: #38bdf8;
}

.capability-content {
  display: flex;
  flex-direction: column;
}

.capability-metric {
  font-size: 0.84rem;
  font-weight: 700;
  color: var(--color-text, #ffffff);
  line-height: 1.2;
}

.capability-planned {
  color: var(--color-text-muted, #94a3b8);
  font-weight: 600;
}

.capability-label {
  font-size: 0.68rem;
  color: var(--color-text-muted, #94a3b8);
  font-weight: 500;
}

.roadmap-link {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.8rem;
  font-weight: 600;
  color: #38bdf8;
  text-decoration: none;
  white-space: nowrap;
}

.roadmap-link:hover {
  text-decoration: underline;
}
</style>
