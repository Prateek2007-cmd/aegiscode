# Runtime architecture

The API composes application modules and provider adapters. Application ports carry provider-neutral
review and crawl evidence; Domain owns durable state and evidence enums; Infrastructure owns PostgreSQL
mapping, migrations and SCM adapters. Review execution remains separate from analytics collection and
queries. ProCursor is accessed through its bounded gateway contract.

| Ownership | Reference |
|---|---|
| Review intake, execution, publication and diagnostics | [Reviews](concepts/reviews.md), [runner architecture](reference/runner-architecture.md) |
| Repository targets, guarded review admission and shared PostgreSQL discovery observations | [Repository targets](reference/api.md#repository-review-targets), [pull request discovery](reference/api.md#pull-request-discovery) |
| Code Insights evidence, projections, score queries and reports | [Code Insights](architecture/code-insights.md) |
| SCM contracts, local declarations and native capability ownership | [SCM provider boundaries](architecture/scm-provider-boundaries.md) |
| Credentials and tenant isolation | [Security](reference/security.md) |
| Durable deployment state and backups | [Upgrades and backups](operate/upgrades-and-backups.md) |

## SCM configuration

The API owns request bounds, egress checks, caller authorization and licensing guards.
Application services coordinate connection validation, verification and review-source selection
through existing provider capabilities. Native authentication requirements belong to each
provider's `IScmConnectionConfigurationPolicy`; policy inputs contain metadata and credential
availability, not credential values. Application and installation identifiers are typed neutral values,
and native policies prepare candidate and persisted usernames. The Clients module registers the required configuration
coordinator, and controllers receive it through its Application interface.

The API owns management identifiers, public defaults, aliases and HTTP compatibility shapes.
Infrastructure owns native enum mappings, recorded-provider decoding and historical source projections.
Application consumes resolved identifiers and normalized evidence through neutral contracts. These mappings do not require credentials, provider activation or
runtime adapter resolution. Saved targets remain readable when their provider is unavailable.
Guided discovery addresses an active client-owned connection and requires an explicit configuration
purpose. Native capabilities supply hierarchy descriptors and persistent selection coordinates.
Native calls use credentials for the selected connection. Identity lookup preserves configured scope
selection and credential ownership.

Native modules register credential-free source, configuration, identity, review-preparation and
webhook-ingress declarations independently of database and live-adapter registration. Local policy
availability does not establish runtime capability or provider activation. Persistence and author
counting consume only the required pure declarations, without constructing live SCM services.
Native account facts are inputs to the fixed licensing classifier; they do not configure exclusions
or change author-count enforcement.

Native source policies prepare captured namespace, repository and symbol-query coordinates.
Unknown saved identities use explicit compatibility projections without enabling provider operations.
Infrastructure's credential-free compatibility codec preserves saved native identifiers and providerless
status evidence. Explicit job construction receives captured source coordinates; historical missing-context
construction is prepared at the native boundary.
The API composes native browser-origin declarations with configured origins; its existing CORS and
redirect checks retain control of browser access.

## Review preparation and bounded context

Native review preparation supplies stored revision aliases, comparison context, anchors and thread
intent before publication policy evaluation and result persistence. Suppressed SCM publication still
records the prepared internal review result and diagnostics. Live revision refresh retains its
existing operation and cancellation boundaries.

Evidence verification uses one reasoning-inclusive output-budget retry within the existing claim
deadline and repository-lookup limits. Withheld candidates remain separate from publishable comments.
Per-file checkpoints and completed job JSON persist their comment and reason independently of analytics.
Synthesis and runner transport retain that evidence for authorized Code Insights collection as
`PolicyWithheld`. Collection does not supply publication receipts or settled outcome credit for
those candidates.

Automatic judge effort requires both model and transport support. The provider catalog supplies exact
model effort facts; the driver port confirms the selected transport. Resolved runtimes and optional
runner bindings carry that capability. Unknown or incompatible bindings use provider defaults.

The local ProCursor SCM broker validates client existence before delegating credential and SDK work
through the existing bounded broker port. Azure DevOps owns the native backend. Review-context tools
consume neutral prepared symbol coordinates; absent coordinates remain unavailable and do not enable
additional provider families. Runner symbol requests use its existing proxy boundary.

## Crawl synchronization and locks

Manual pull request overview refreshes consume the optional paged capability on the existing review
discovery adapters. One fenced source refresh owns the entire native page sequence and its shared
60-second cooldown. Native pagination headers and continuations remain inside provider adapters.
The rich page contract distinguishes complete upstream listing coverage from collected row counts and
visible-row metadata completeness. Deadline and storage limits produce incomplete coverage.

Scheduled discovery traverses native pages for Azure DevOps, GitHub, GitLab and Forgejo through the
registered adapters. Saved canonical filters retain repository identity; legacy names resolve uniquely
within the saved project, owner or group. Complete observations precede synchronization. A page failure,
throttle, cancellation or traversal bound prevents absent-job maintenance for that configuration.

The crawler requires the shared pull-request synchronizer. It uses that service for discovered
pull requests and lifecycle checks on jobs absent from discovery. Scheduled intake selects one owned,
active, verified connection with matching grants and captures its non-secret configuration and configured
reviewer identity. Active alternative selectors cause refusal. A short target admission lease validates
saved target identity and authority before provider preparation. Preparation runs without the lease,
so target mutations can complete during provider reads. A final lease covers current target and authority
checks and queue admission. Connection edits do not share that lease. Queued credential resolution may observe later
rotation. Known native closed or merged status permits absent-job maintenance; missing or unknown status
does not establish closure.
The synchronizer owns duplicate-safe job persistence, revision decisions and thread maintenance.

Automatic-review activity is independent of manual-review lifecycle. The revision-conditional automation
endpoint changes only activity and revision. Enabling requires current eligible connection authority;
pausing an owned saved target does not require credentials or current grants. Creation, restore and
manual lifecycle enable leave automatic reviews paused. Successful automation responses contain the
saved target snapshot confirmed under the operation's admission lease. Later mutations do not change
that response's activity or revision.

PostgreSQL advisory locks use shared helpers with explicit bigint, `hashtext` and two-integer key
protocols. Callers own key derivation, transactions, isolation and retries. Transaction locks retain
the caller's transaction lifetime; owned session leases retain separate acquisition and disposal.
