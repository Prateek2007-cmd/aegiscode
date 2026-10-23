# AegisCode security-engine demo fixture

**All secrets in this directory are synthetic test data.** They match no real account,
key, or credential. They exist solely so the secret-detection demo has something
genuine (but harmless) to find.

- `package.json` / `package-lock.json` — pins `lodash@4.17.20`, which has real,
  publicly documented CVEs (ReDoS, command injection, prototype pollution).
  OSV.dev returns genuine advisories for it.
- `config/deploy.env` — two fake credentials shaped to match gitleaks'
  `aws-access-token` and `generic-api-key` rules (verified against the real
  gitleaks v8.27.2 default config on 2026-10-23).

If your own secret scanner flags this directory, that is the *correct* behavior —
it proves the scanner works. Do not "rotate" these values; they were never real.

See `../DEMO_SCRIPT.md` for the reproducible walkthrough and
`../aegiscode-demo-report.json` for the real scan output produced from this fixture.
