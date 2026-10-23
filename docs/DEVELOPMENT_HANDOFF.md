# ProPR Developer Handoff & Architecture Guide

This document prepares the ProPR codebase for continued engineering and UI redesign in **Muse**. It provides an accurate, code-backed technical overview of the architecture, entry points, configuration, database models, authentication schemes, existing integrations, and extension points for subsequent hackathon security features.

---

## 1. Project Architecture & Directory Structure

ProPR is structured as a modular monolith control plane with an extracted microservice host for semantic code indexing (**ProCursor**) and a reactive Vue 3 SPA (**Frontend**).

```
.
├── src/                                  # .NET 10 backend solutions & libraries (32 projects)
│   ├── MeisterDev.ProPR.Api/             # Composition root, HTTP controllers, workers, middleware
│   ├── MeisterDev.ProPR.Application/     # Orchestrators, CQRS handlers, domain services, interfaces
│   ├── MeisterDev.ProPR.Domain/          # Core domain models (ReviewJob, PullRequest, AiConnection, etc.)
│   ├── MeisterDev.ProPR.Infrastructure/  # EF Core DbContext, PostgreSQL persistence, SCM adapters
│   ├── MeisterDev.ProPR.Reviewing/       # AI review engine, file-by-file orchestrator, workspace manager
│   ├── MeisterDev.ProPR.ProCursor/       # Knowledge source indexing, embedding, and code search
│   ├── MeisterDev.ProPR.ProCursor.Service/# Standalone microservice host for ProCursor operations
│   ├── MeisterDev.ProPR.ProCursor.Contracts/# Shared DTOs and interfaces across ProPR <-> ProCursor
│   ├── MeisterDev.ProPR.CodeInsights/    # Analytics, precision/recall metrics, reviewer performance
│   ├── MeisterDev.ProPR.CodeAnalysis/    # Code analysis abstractions (Roslyn & TreeSitter)
│   ├── MeisterDev.Ai.Providers/          # AI provider adapters (OpenAI, Azure, Anthropic, Bedrock, Vertex, LiteLLM)
│   ├── MeisterDev.DataProtection/        # Keyring protection and encryption codecs
│   ├── MeisterDev.ProPR.Runner/          # Distributed review runner execution engine
│   └── MeisterDev.ProPR.Observability/   # Serilog structured logging, Prometheus metrics, Loki sinks
├── frontend/                             # Vue 3 SPA (Vite 8, Vuetify 4, TypeScript)
│   ├── src/
│   │   ├── app/                          # App runtime bootstrap and providers
│   │   ├── components/                   # Shared UI components (dialogs, cards, navigation)
│   │   ├── composables/                  # Shared state & hooks (useSession, useNotification)
│   │   ├── features/                     # Feature slices (clients, reviews, licensing, pats, tenants)
│   │   ├── mocks/                        # MSW mock server handlers and fixtures
│   │   ├── services/                     # Typed service clients wrapping openapi-fetch
│   │   └── types/                        # TypeScript types (generated from openapi.json)
│   ├── tests/                            # Vitest unit/component tests & Playwright E2E suites
│   └── package.json                      # npm configuration & scripts
├── tests/                                # 30 backend xUnit test projects
├── scripts/                              # Local dev scripts, runners, and webhook simulators
├── docs/                                 # Architectural specifications and user guides
├── example/                              # Docker Compose and Azure Container Apps deployment templates
├── openapi.json                          # Authoritative OpenAPI 3.0 specification
└── MeisterDev.ProPR.slnx                 # .NET solution file (61 projects)
```

---

## 2. Frontend & Backend Entry Points

### Backend API (`MeisterDev.ProPR.Api`)
- **Entry Point**: `src/MeisterDev.ProPR.Api/Program.cs` calling `ProPRHost.RunAsync(args)`.
- **Default Port**: `8080` (HTTP).
- **Composition Root**: In `ProPRHost.cs`, modular services are registered in strict dependency order:
  - Database & Data Protection (`AddInfrastructure()`, `MeisterProPRDbContext`)
  - AI Providers & Plugins (`AddAiProviders()`)
  - Vertical Feature Modules: `AddClientsModule()`, `AddReviewingModule()`, `AddCrawlingModule()`, `AddIdentityAndAccessModule()`, `AddMentionsModule()`, `AddPromptCustomizationModule()`, `AddUsageReportingModule()`, `AddLicensingModule()`.
  - ProCursor Gateway binding (`proprManagedRemote` vs `disabled`).

### ProCursor Service (`MeisterDev.ProPR.ProCursor.Service`)
- **Entry Point**: `src/MeisterDev.ProPR.ProCursor.Service/Program.cs`.
- **Default Port**: `8081` (HTTP).
- **Purpose**: Extracted service that manages vector embeddings, pgvector operational storage, and repository knowledge indexing independently of review execution.

### Frontend SPA (`frontend`)
- **Entry Point**: `frontend/src/main.ts` mounted onto `frontend/index.html`.
- **Default Port**: `5173` (Vite dev server).
- **Routing**: `frontend/src/router/index.ts` defines route-driven administration (`/clients`, `/reviews`, `/settings`, `/tenants`, `/provider-settings`).
- **State**: Reactive session state in `frontend/src/composables/useSession.ts` tracks JWT tokens, tenant roles, client roles, and licensing capabilities.

---

## 3. Available Scripts & Startup Commands

| Task | Command | Description |
|---|---|---|
| **Full Local Stack** | `.\scripts\run-local.ps1` | Launches PostgreSQL checks, builds .NET projects, starts API (8080), ProCursor (8081), and Frontend (5173). |
| **Full Stack (Bash)** | `./scripts/run-local.sh` | Linux/macOS bash equivalent of `run-local.ps1`. |
| **Dev Environment (Podman/Nginx)** | `.\scripts\Start-DevEnvironment.ps1` | Builds frontend and starts Nginx proxy on port 5080 with backend integration. |
| **Backend API Only** | `dotnet run --project src/MeisterDev.ProPR.Api/MeisterDev.ProPR.Api.csproj` | Starts the API host locally. Requires `DB_CONNECTION_STRING`. |
| **ProCursor Host Only** | `dotnet run --project src/MeisterDev.ProPR.ProCursor.Service/MeisterDev.ProPR.ProCursor.Service.csproj` | Starts ProCursor microservice on port 8081. |
| **Frontend Dev Server** | `cd frontend && npm run dev` | Starts Vite dev server with proxying of `/api` requests to `http://localhost:8080`. |
| **Frontend Mock Mode** | `cd frontend && npm run mock` | Starts frontend in standalone mock mode using MSW (no backend or database needed). |
| **Generate OpenAPI Spec** | `$env:DB_CONNECTION_STRING="Host=localhost;Database=test;Username=postgres;Password=pass"; dotnet run --project src/MeisterDev.ProPR.Api/MeisterDev.ProPR.Api.csproj --no-build -- --generate-openapi` | Generates root `openapi.json` directly from controller annotations. |
| **Generate Frontend API Types** | `cd frontend && npm run generate:api` | Regenerates TypeScript client types from `openapi.json`. |
| **Run Backend Tests** | `dotnet test MeisterDev.ProPR.slnx` | Executes backend xUnit suites. |
| **Run Frontend Tests** | `cd frontend && npm test` | Runs 199 Vitest suites with jsdom. |
| **Run Frontend E2E Tests** | `cd frontend && npm run test:e2e` | Executes Playwright end-to-end browser tests. |

---

## 4. API Routes & Endpoint Architecture

The API exposes RESTful endpoints grouped by domain feature slices:

### Identity & Authentication
- `POST /auth/login`: Authenticate with username/password, returns JWT and refresh token.
- `POST /auth/refresh`: Rotate refresh token and obtain a new short-lived JWT.
- `POST /auth/change-password`: Change user password.
- `GET/POST /auth/pats`, `DELETE /auth/pats/{id}`: List, generate, and revoke Personal Access Tokens.
- `GET/POST/PUT /tenants/{id}/sso`: Manage OpenID Connect / SAML SSO configurations per tenant.
- `GET/POST /tenants/{id}/machine-credentials`: Issue machine runner tokens.

### Client Management & Provider Connections
- `GET/POST/PATCH /clients`: List, register, and update tenant clients.
- `GET/POST/DELETE /clients/{id}/connections/scm`: Manage Azure DevOps, GitHub, GitLab, and Forgejo connections.
- `GET/POST/DELETE /clients/{id}/connections/ai`: Manage BYOK AI provider connections (OpenAI, Azure, Bedrock, etc.).
- `GET/PUT /clients/{id}/reviewer-identity`: Configure SCM bot/reviewer identity for automatic review assignments.

### Review Orchestration & History
- `GET /jobs`: Filter and paginate review jobs (`Pending`, `Processing`, `Completed`, `Failed`).
- `GET /jobs/{id}`: Detailed review job metadata, duration, token usage, and status.
- `GET /jobs/{id}/events`: Event stream and protocol audit log (AI iterations, tool calls, filter decisions).
- `GET /reviews/{id}/diff`: Retrieve file diffs associated with a review.
- `GET /reviews/{id}/findings`: Retrieve AI-generated findings, severities, and triage statuses.
- `POST /reviews/{id}/findings/{findingId}/dismiss`: Triage or dismiss a finding with audit reasoning.

### Webhooks & Autonomous Crawling
- `POST /webhooks/{provider}`: Ingress endpoint for incoming SCM webhooks (PR created, updated, commented).
- `GET/POST/PUT/DELETE /admin/crawls`: Scheduled repository crawl configurations for polling active PRs.
- `GET/POST/PUT/DELETE /admin/webhooks`: Managed webhook registrations.

### ProCursor Knowledge Base
- `GET/POST/DELETE /procursor/sources`: Manage indexed repository knowledge sources.
- `POST /procursor/search`: Query semantic code search using vector embeddings.

### Runners & Distributed Execution
- `POST /runners/register`: Register an external self-hosted review runner.
- `POST /runners/lease`: Lease pending review jobs to available runners.
- `POST /runners/{id}/execution`: Upload job results and execution traces from runners.

### Licensing & Health
- `GET /admin/license`: View current edition (Community vs Commercial) and active capabilities.
- `POST /admin/license/activate`: Activate commercial license key offline via digital signature.
- `GET /healthz`, `/livez`, `/readyz`: Container liveness and readiness health checks.

---

## 5. Database & Authentication Details

### Database: PostgreSQL + pgvector
- **Engine**: PostgreSQL 17 with `pgvector/pgvector:pg17` extension enabled.
- **ORM**: Entity Framework Core 10 (`MeisterProPRDbContext`) using Npgsql provider.
- **Schemas & Migration**:
  - Automatically migrates on startup when `DB_CONNECTION_STRING` is configured.
  - Logical separation: ProPR and ProCursor share the same physical database by default, or separate DBs when `PROCURSOR_DB_CONNECTION_STRING` is set.
  - JSONB columns are used for complex payloads (e.g. review event data, provider credentials, and token breakdowns).

### Authentication & Secrets Model
1. **User JWTs**:
   - HS256 JWTs signed using `MEISTER_JWT_SECRET` (minimum 32 characters).
   - Scoped claims include `sub` (User ID), `role` (Admin/User), `clientRoles`, and `tenantRoles`.
   - Refresh tokens are stored by **SHA-256 hash** in the database; plaintext refresh tokens exist only in the client.
2. **Personal Access Tokens (PATs)**:
   - Generated as high-entropy bearer tokens, stored using **BCrypt** password hashes.
3. **Internal ProCursor Key**:
   - Inter-service communication between ProPR API and ProCursor service uses the header `X-ProCursor-Key: <PROCURSOR_SHARED_KEY>`.
4. **Secrets at Rest**:
   - AI API keys, SCM tokens, and webhook secrets are protected using ASP.NET Core Data Protection (`ISecretProtectionCodec`).
   - Keys are persisted in XML format under `MEISTER_DATA_PROTECTION_KEYS_PATH`.
   - Responses to `GET` endpoints **never** expose secret values (write-only model).

---

## 6. Required Environment Variables

A full template is provided at [`.env.example`](file:///.env.example). Required variables include:

```env
# Core Secrets (Must be at least 32 characters)
MEISTER_JWT_SECRET=change-me-to-a-long-random-secret-at-least-32-chars
PROCURSOR_SHARED_KEY=change-me-to-a-long-random-secret-at-least-32-chars

# Bootstrap Administrator
MEISTER_BOOTSTRAP_ADMIN_USER=admin
MEISTER_BOOTSTRAP_ADMIN_PASSWORD=change-me-secure-password

# Database Connections
DB_CONNECTION_STRING=Host=localhost;Port=5432;Database=meisterpropr;Username=postgres;Password=devpass
PROCURSOR_DB_CONNECTION_STRING=Host=localhost;Port=5432;Database=meisterpropr;Username=postgres;Password=devpass

# ProCursor Coordination
PROCURSOR_REMOTE_MODE=proprManagedRemote
PROCURSOR_SERVICE_BASE_URL=http://localhost:8081
PROCURSOR_PROPR_BASE_URL=http://localhost:8080

# Ingress & Networking
CORS_ORIGINS=http://localhost:5173
MEISTER_PUBLIC_BASE_URL=http://localhost:8080/api

# Frontend (Vite)
VITE_API_BASE_URL=/api
```

---

## 7. Existing Integrations & Dependencies

### SCM Platforms
- **Azure DevOps**: PAT and Azure OAuth support; Git repository inspection; comment thread creation and updates.
- **GitHub**: Personal Access Token & GitHub App authentication; GitHub Webhooks; review comments and PR reviews.
- **GitLab**: Project Access Tokens / Personal Access Tokens; GitLab discussion notes.
- **Forgejo / Codeberg**: Gitea-family API integration for self-hosted instances.

### AI Model Providers (BYOK)
- **Azure OpenAI / AI Foundry**: Native completions & embeddings with Managed Identity or API Key.
- **OpenAI**: Direct integration with GPT-4o, o1, o3-mini models.
- **Anthropic**: Claude 3.5 Sonnet and Haiku native adapter.
- **AWS Bedrock**: IAM credentials / assume role authentication.
- **Google Vertex AI / Gemini**: Google Cloud project & service account credentials.
- **LiteLLM / OpenAI Compatible**: Custom gateway proxying to any compliant endpoint.

### Analysis & Parser Libraries
- **TreeSitter.DotNet (1.3.0)**: Native AST parsing for multi-language syntax awareness.
- **Microsoft.CodeAnalysis (Roslyn)**: Deep semantic inspection for C# codebases.
- **Diff2Html & DOMPurify**: Safe HTML diff rendering and sanitization in frontend.

---

## 8. Verified Setup Fixes & Known Technical Debt

### Resolved Setup & Platform Fixes
- **Windows File Attribute Handling**: Resolved `UnauthorizedAccessException` during repository directory cleanup by clearing read-only attributes on Git objects before `Directory.Delete`.
- **Git CRLF Line Ending Consistency**: Configured `core.autocrlf=false` on worktree additions in `GitReviewRepositoryWorkspaceManager` to eliminate line-ending mismatches across operating systems.
- **Frontend Timezone Robustness**: Fixed localized date expectation in `usePatsViewModel.spec.ts` to compute ISO strings dynamically rather than hardcoding UTC+0/UTC+1 assumptions.
- **Vitest Concurrency Headroom**: Increased `testTimeout` to 30000ms in `vite.config.ts` to accommodate heavy jsdom view mounting under high parallel load.
- **Offline DI Fallback**: Added `NoOpScmProviderRegistry` fallback in `ClientsModuleServiceCollectionExtensions` when running without a database connection.
- **Vite Cache Cleanup**: Untracked inadvertently committed `frontend/.vite/` cache artifacts and added `.vite/` and `.dev-env/` to `.gitignore`.

### Known Limitations & Technical Debt
- **PostgreSQL Vector Prerequisite**: Migrations require the PostgreSQL `vector` extension (`CREATE EXTENSION IF NOT EXISTS vector;`) to exist prior to running migrations.
- **Commercial Capability Gates**: Commercial features (SSO, distributed runners, multi-tenancy) require valid offline license files signed against the embedded root certificate; they cannot be toggled purely via configuration.

---

## 9. Recommended Extension Points for Future Security Features

For Muse's subsequent hackathon security features, the following architecture seams are recommended:

1. **AI Prompt Injection & Exfiltration Guardrails**:
   - **Extension Point**: `ICommentRelevanceFilter` and `IReviewVerifier` (`src/MeisterDev.ProPR.Reviewing`).
   - **Recommended Implementation**: Intercept file diffs before prompt construction in `FileByFileReviewOrchestrator` to detect indirect prompt injections, canary leaks, or jailbreak attempts.
2. **Secret Scanning in Pull Requests**:
   - **Extension Point**: `FileReviewer` pass in `MeisterDev.ProPR.Reviewing.Features.Reviewing.Execution.Strategies.FileByFile`.
   - **Recommended Implementation**: Integrate high-entropy secret detection (regex + Shannon entropy checks) before AI submission to prevent transmitting sensitive tokens to external LLMs.
3. **Webhook Authenticity & Anti-Replay Shield**:
   - **Extension Point**: `ProviderWebhookReceiverController` and `IWebhookIngressPolicy` (`src/MeisterDev.ProPR.Infrastructure.Features.Providers.*`).
   - **Recommended Implementation**: Add nonce tracking, strict timestamp window validation, and rate limiting to prevent webhook replay attacks.
4. **KMS / HSM Hardware Secret Storage**:
   - **Extension Point**: `ISecretProtectionCodec` in `MeisterDev.DataProtection`.
   - **Recommended Implementation**: Provide pluggable KMS key decryption providers (e.g., AWS KMS, HashiCorp Vault, GCP Cloud KMS) alongside the existing Azure Key Vault add-in.
5. **Signed Review Evidence & Provenance**:
   - **Extension Point**: `CodeInsightHistoricalRefreshStore` and `IReviewOrchestrationService`.
   - **Recommended Implementation**: Cryptographically sign review summary artifacts using an internal ephemeral private key before posting to the SCM host for cryptographic non-repudiation.
