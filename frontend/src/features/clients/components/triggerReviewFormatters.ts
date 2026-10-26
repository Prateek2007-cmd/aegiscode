// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Pure helpers for the Trigger Review card. No API calls, no invented data:
 * every helper parses or describes a value the user typed or the API returned.
 */
import type { PullRequestIdentity, ReviewByCoordinatesResult } from '@/services/jobsService'

export const DEFAULT_PROVIDER_HOST = 'https://github.com'

/** Accepts `owner/repo` or a full GitHub URL; anything else is null. */
export function parseRepositoryInput(raw: string): { owner: string; repo: string } | null {
  const trimmed = raw.trim()
  if (!trimmed || /\s/.test(trimmed)) return null

  let path = trimmed
  const urlMatch = /^(?:https?:\/\/)?(?:www\.)?github\.com\/(.+)$/i.exec(trimmed)
  if (urlMatch) {
    path = urlMatch[1]
  } else if (/^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed)) {
    // Some other absolute URL — not a GitHub repository reference.
    return null
  }

  const withoutTrailingSlash = path.replace(/\/+$/, '')
  const withoutGit = withoutTrailingSlash.replace(/\.git$/i, '')
  const parts = withoutGit.split('/')
  if (parts.length !== 2) return null
  const [owner, repo] = parts
  if (!owner || !repo) return null
  return { owner, repo }
}

/** Error message when the repository input is not usable, or null when it is valid. */
export function validateRepositoryInput(raw: string): string | null {
  return parseRepositoryInput(raw) ? null : 'Enter a repository like owner/repo'
}

/** Error message when the PR number is not a positive integer, or null when it is valid. */
export function validatePullRequestNumber(raw: string): string | null {
  const trimmed = raw.trim()
  if (!/^\d+$/.test(trimmed)) return 'Enter a positive PR number'
  return Number.parseInt(trimmed, 10) > 0 ? null : 'Enter a positive PR number'
}

/**
 * Builds the identity the review endpoint needs from a repository reference
 * and a PR number. Callers validate first; unparseable input throws.
 */
export function buildPullRequestIdentity(
  providerScopePath: string,
  repository: string,
  pullRequestId: number,
): PullRequestIdentity {
  const parsed = parseRepositoryInput(repository)
  if (!parsed) {
    throw new Error('Enter a repository like owner/repo')
  }
  if (!Number.isInteger(pullRequestId) || pullRequestId <= 0) {
    throw new Error('Enter a positive PR number')
  }
  return {
    providerScopePath,
    providerProjectKey: parsed.owner,
    repositoryId: `${parsed.owner}/${parsed.repo}`,
    pullRequestId,
  }
}

export interface ReviewOutcomeDescription {
  title: string
  detail: string
  tone: 'success' | 'warning' | 'error'
}

function withReason(detail: string, reason?: string | null): string {
  return reason ? `${detail} (${reason})` : detail
}

/** Plain-language, honest description of a named review-submission outcome. */
export function describeReviewOutcome(result: ReviewByCoordinatesResult): ReviewOutcomeDescription {
  switch (result.outcome) {
    case 'submitted':
      return {
        title: 'Review requested.',
        detail: withReason(
          'The review job was submitted. Watch its progress on the job page.',
          result.reason,
        ),
        tone: 'success',
      }
    case 'duplicateActiveJob':
      return {
        title: 'A review is already running for this pull request.',
        detail: withReason(
          'Wait for the running review to finish before requesting another one.',
          result.reason,
        ),
        tone: 'warning',
      }
    case 'notAuthorized':
      return {
        title:
          'No webhook or crawl config covers this repository for this client — add a webhook or crawl config for it first.',
        detail: withReason(
          'Add a webhook or crawl config covering this repository, then try again.',
          result.reason,
        ),
        tone: 'error',
      }
    case 'pullRequestNotFound':
      return {
        title: 'That pull request was not found on the provider — check the repository and PR number.',
        detail: withReason('Check the repository and PR number and try again.', result.reason),
        tone: 'error',
      }
    case 'revisionUnresolvable':
      return {
        title: "The pull request's current revision could not be resolved.",
        detail: withReason(
          'Try again — the provider may not have the latest revision yet.',
          result.reason,
        ),
        tone: 'error',
      }
    case 'notSubmittable':
      return {
        title: 'That pull request cannot be reviewed — it must be open.',
        detail: withReason('Only open pull requests can be reviewed.', result.reason),
        tone: 'error',
      }
    case 'submissionFailed':
    default:
      return {
        title: 'The review could not be submitted.',
        detail: withReason(
          'The review could not be submitted. Try again, and check the provider connection if it keeps failing.',
          result.reason,
        ),
        tone: 'error',
      }
  }
}
