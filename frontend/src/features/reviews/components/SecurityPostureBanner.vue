<template>
  <section class="security-posture-banner" aria-label="Security Posture and Guardrail Status">
    <div class="posture-main">
      <div class="posture-score-cluster">
        <div class="score-ring">
          <svg viewBox="0 0 36 36" class="circular-chart">
            <path class="circle-bg"
              d="M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831"
            />
            <path class="circle-val"
              stroke-dasharray="98, 100"
              d="M18 2.0845 a 15.9155 15.9155 0 0 1 0 31.831 a 15.9155 15.9155 0 0 1 0 -31.831"
            />
          </svg>
          <div class="score-text">
            <span class="score-number">98</span>
            <span class="score-grade">A+</span>
          </div>
        </div>
        <div class="score-info">
          <div class="score-title-row">
            <h3 class="posture-title">Security Posture Index</h3>
            <span class="defense-status-badge">
              <span class="status-pulse-dot"></span>
              Active Defense
            </span>
          </div>
          <p class="posture-subtitle">
            Enterprise guardrails actively scanning incoming PRs across code AST, supply chain, and secrets.
          </p>
        </div>
      </div>

      <div class="guardrails-grid">
        <div class="guardrail-card">
          <div class="guardrail-icon-box guardrail-icon-secrets">
            <i class="fi fi-rr-key"></i>
          </div>
          <div class="guardrail-content">
            <div class="guardrail-metric">0 Leaks</div>
            <div class="guardrail-label">Secret Scanning</div>
          </div>
        </div>

        <div class="guardrail-card">
          <div class="guardrail-icon-box guardrail-icon-owasp">
            <i class="fi fi-rr-shield-check"></i>
          </div>
          <div class="guardrail-content">
            <div class="guardrail-metric">OWASP Top 10</div>
            <div class="guardrail-label">CWE Rules Active</div>
          </div>
        </div>

        <div class="guardrail-card">
          <div class="guardrail-icon-box guardrail-icon-sbom">
            <i class="fi fi-rr-cube"></i>
          </div>
          <div class="guardrail-content">
            <div class="guardrail-metric">0 CVEs</div>
            <div class="guardrail-label">SBOM Guardrails</div>
          </div>
        </div>

        <div class="guardrail-card">
          <div class="guardrail-icon-box guardrail-icon-remediation">
            <i class="fi fi-rr-magic-wand"></i>
          </div>
          <div class="guardrail-content">
            <div class="guardrail-metric">1-Click Fix</div>
            <div class="guardrail-label">AI Remediation</div>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
// AegisCode Security Posture & Guardrails Command Center Banner
</script>

<style scoped>
.security-posture-banner {
  background: linear-gradient(135deg, rgba(15, 23, 42, 0.85) 0%, rgba(17, 24, 39, 0.95) 100%);
  border: 1px solid rgba(56, 189, 248, 0.2);
  border-radius: 14px;
  padding: 1.25rem 1.5rem;
  margin-bottom: 2rem;
  box-shadow: 0 8px 32px -8px rgba(0, 0, 0, 0.5), inset 0 1px 0 rgba(255, 255, 255, 0.05);
  backdrop-filter: blur(12px);
}

.posture-main {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 2rem;
  flex-wrap: wrap;
}

.posture-score-cluster {
  display: flex;
  align-items: center;
  gap: 1.25rem;
  min-width: 280px;
  flex: 1;
}

.score-ring {
  position: relative;
  width: 58px;
  height: 58px;
  flex-shrink: 0;
}

.circular-chart {
  display: block;
  width: 100%;
  height: 100%;
}

.circle-bg {
  fill: none;
  stroke: rgba(255, 255, 255, 0.08);
  stroke-width: 3.2;
}

.circle-val {
  fill: none;
  stroke: #10b981;
  stroke-width: 3.2;
  stroke-linecap: round;
  transition: stroke-dasharray 0.6s ease;
}

.score-text {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  display: flex;
  flex-direction: column;
  align-items: center;
  line-height: 1;
}

.score-number {
  font-size: 0.95rem;
  font-weight: 800;
  color: #ffffff;
}

.score-grade {
  font-size: 0.62rem;
  font-weight: 700;
  color: #10b981;
}

.score-info {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.score-title-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.posture-title {
  margin: 0;
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--color-text, #f1f5f9);
  letter-spacing: -0.01em;
}

.defense-status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.2rem 0.55rem;
  border-radius: 9999px;
  background: rgba(16, 185, 129, 0.14);
  border: 1px solid rgba(16, 185, 129, 0.3);
  font-size: 0.72rem;
  font-weight: 600;
  color: #34d399;
}

.status-pulse-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background-color: #10b981;
  box-shadow: 0 0 8px #10b981;
  animation: pulse-glow 2s infinite;
}

@keyframes pulse-glow {
  0% { transform: scale(0.95); opacity: 0.8; }
  50% { transform: scale(1.3); opacity: 1; }
  100% { transform: scale(0.95); opacity: 0.8; }
}

.posture-subtitle {
  margin: 0;
  font-size: 0.82rem;
  color: var(--color-text-muted, #94a3b8);
  max-width: 50ch;
  line-height: 1.4;
}

.guardrails-grid {
  display: flex;
  align-items: center;
  gap: 0.85rem;
  flex-wrap: wrap;
}

.guardrail-card {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.6rem 0.85rem;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 10px;
  transition: all 0.2s ease;
}

.guardrail-card:hover {
  background: rgba(56, 189, 248, 0.06);
  border-color: rgba(56, 189, 248, 0.25);
  transform: translateY(-1px);
}

.guardrail-icon-box {
  width: 32px;
  height: 32px;
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 0.95rem;
}

.guardrail-icon-secrets {
  background: rgba(244, 63, 94, 0.15);
  color: #fb7185;
}

.guardrail-icon-owasp {
  background: rgba(56, 189, 248, 0.15);
  color: #38bdf8;
}

.guardrail-icon-sbom {
  background: rgba(168, 85, 247, 0.15);
  color: #c084fc;
}

.guardrail-icon-remediation {
  background: rgba(16, 185, 129, 0.15);
  color: #34d399;
}

.guardrail-content {
  display: flex;
  flex-direction: column;
}

.guardrail-metric {
  font-size: 0.84rem;
  font-weight: 700;
  color: var(--color-text, #ffffff);
  line-height: 1.2;
}

.guardrail-label {
  font-size: 0.68rem;
  color: var(--color-text-muted, #94a3b8);
  font-weight: 500;
}
</style>
