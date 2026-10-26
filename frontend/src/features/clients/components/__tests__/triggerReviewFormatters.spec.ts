// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  buildPullRequestIdentity,
  describeReviewOutcome,
  parseRepositoryInput,
  validatePullRequestNumber,
  validateRepositoryInput,
} from '../triggerReviewFormatters'
import type { ReviewByCoordinatesResult } from '@/services/jobsService'

describe('parseRepositoryInput', () => {
  it('parses a valid owner/repo', () => {
    expect(parseRepositoryInput('Prateek2007-cmd/PRIVAGENT')).toEqual({
      owner: 'Prateek2007-cmd',
      repo: 'PRIVAGENT',
    })
  })

  it('trims surrounding whitespace', () => {
    expect(parseRepositoryInput('  Prateek2007-cmd/ACM  ')).toEqual({
      owner: 'Prateek2007-cmd',
      repo: 'ACM',
    })
  })

  it('extracts owner/repo from a full GitHub URL', () => {
    expect(parseRepositoryInput('https://github.com/Prateek2007-cmd/ACM')).toEqual({
      owner: 'Prateek2007-cmd',
      repo: 'ACM',
    })
  })

  it('tolerates a trailing slash on a GitHub URL', () => {
    expect(parseRepositoryInput('https://github.com/Prateek2007-cmd/ACM/')).toEqual({
      owner: 'Prateek2007-cmd',
      repo: 'ACM',
    })
  })

  it('strips a .git suffix from a GitHub URL', () => {
    expect(parseRepositoryInput('https://github.com/Prateek2007-cmd/ACM.git')).toEqual({
      owner: 'Prateek2007-cmd',
      repo: 'ACM',
    })
  })

  it('returns null for an empty input', () => {
    expect(parseRepositoryInput('')).toBeNull()
    expect(parseRepositoryInput('   ')).toBeNull()
  })

  it('returns null when there is no slash', () => {
    expect(parseRepositoryInput('just-a-repo')).toBeNull()
  })

  it('returns null for three segments', () => {
    expect(parseRepositoryInput('owner/repo/extra')).toBeNull()
    expect(parseRepositoryInput('https://github.com/owner/repo/extra')).toBeNull()
  })

  it('returns null when either part is empty', () => {
    expect(parseRepositoryInput('/repo')).toBeNull()
    expect(parseRepositoryInput('owner/')).toBeNull()
    expect(parseRepositoryInput('/')).toBeNull()
  })

  it('returns null when the input contains spaces', () => {
    expect(parseRepositoryInput('my owner/repo')).toBeNull()
    expect(parseRepositoryInput('owner/my repo')).toBeNull()
  })

  it('returns null for a non-GitHub URL', () => {
    expect(parseRepositoryInput('https://gitlab.com/owner/repo')).toBeNull()
  })
})

describe('validateRepositoryInput', () => {
  it('returns null for a valid repository', () => {
    expect(validateRepositoryInput('owner/repo')).toBeNull()
    expect(validateRepositoryInput('https://github.com/owner/repo.git')).toBeNull()
  })

  it('returns an error message for invalid input', () => {
    expect(validateRepositoryInput('')).toBe('Enter a repository like owner/repo')
    expect(validateRepositoryInput('owner')).toBe('Enter a repository like owner/repo')
    expect(validateRepositoryInput('a/b/c')).toBe('Enter a repository like owner/repo')
  })
})

describe('validatePullRequestNumber', () => {
  it('accepts a positive integer', () => {
    expect(validatePullRequestNumber('1')).toBeNull()
    expect(validatePullRequestNumber('4929')).toBeNull()
    expect(validatePullRequestNumber('  7  ')).toBeNull()
  })

  it('rejects zero', () => {
    expect(validatePullRequestNumber('0')).toBe('Enter a positive PR number')
  })

  it('rejects negative numbers', () => {
    expect(validatePullRequestNumber('-3')).toBe('Enter a positive PR number')
  })

  it('rejects non-numeric input', () => {
    expect(validatePullRequestNumber('abc')).toBe('Enter a positive PR number')
    expect(validatePullRequestNumber('12a')).toBe('Enter a positive PR number')
  })

  it('rejects decimals', () => {
    expect(validatePullRequestNumber('1.5')).toBe('Enter a positive PR number')
  })

  it('rejects empty input', () => {
    expect(validatePullRequestNumber('')).toBe('Enter a positive PR number')
    expect(validatePullRequestNumber('   ')).toBe('Enter a positive PR number')
  })
})

describe('buildPullRequestIdentity', () => {
  it('uses the owner as the project key and the full owner/repo as the repository id', () => {
    expect(buildPullRequestIdentity('https://github.com', 'Prateek2007-cmd/PRIVAGENT', 1)).toEqual({
      providerScopePath: 'https://github.com',
      providerProjectKey: 'Prateek2007-cmd',
      repositoryId: 'Prateek2007-cmd/PRIVAGENT',
      pullRequestId: 1,
    })
  })

  it('accepts a full GitHub URL as the repository', () => {
    const identity = buildPullRequestIdentity(
      'https://github.com',
      'https://github.com/Prateek2007-cmd/ACM.git',
      1,
    )
    expect(identity.providerProjectKey).toBe('Prateek2007-cmd')
    expect(identity.repositoryId).toBe('Prateek2007-cmd/ACM')
  })

  it('keeps the provider scope path as given', () => {
    const identity = buildPullRequestIdentity('https://ghe.example.com', 'owner/repo', 42)
    expect(identity.providerScopePath).toBe('https://ghe.example.com')
    expect(identity.pullRequestId).toBe(42)
  })

  it('throws on an unparseable repository', () => {
    expect(() => buildPullRequestIdentity('https://github.com', 'not-a-repo', 1)).toThrow()
  })

  it('throws on a non-positive PR number', () => {
    expect(() => buildPullRequestIdentity('https://github.com', 'owner/repo', 0)).toThrow()
  })
})

describe('describeReviewOutcome', () => {
  const cases: Array<{
    result: ReviewByCoordinatesResult
    title: string
    tone: 'success' | 'warning' | 'error'
  }> = [
    { result: { outcome: 'submitted', jobId: 'job-1' }, title: 'Review requested.', tone: 'success' },
    {
      result: { outcome: 'duplicateActiveJob', jobId: 'job-1' },
      title: 'A review is already running for this pull request.',
      tone: 'warning',
    },
    {
      result: { outcome: 'notAuthorized' },
      title:
        'No webhook or crawl config covers this repository for this client — add a webhook or crawl config for it first.',
      tone: 'error',
    },
    {
      result: { outcome: 'pullRequestNotFound' },
      title: 'That pull request was not found on the provider — check the repository and PR number.',
      tone: 'error',
    },
    {
      result: { outcome: 'revisionUnresolvable' },
      title: "The pull request's current revision could not be resolved.",
      tone: 'error',
    },
    {
      result: { outcome: 'notSubmittable' },
      title: 'That pull request cannot be reviewed — it must be open.',
      tone: 'error',
    },
    {
      result: { outcome: 'submissionFailed' },
      title: 'The review could not be submitted.',
      tone: 'error',
    },
  ]

  it.each(cases)('describes $result.outcome as $tone', ({ result, title, tone }) => {
    const described = describeReviewOutcome(result)
    expect(described.title).toBe(title)
    expect(described.tone).toBe(tone)
    expect(described.detail.length).toBeGreaterThan(0)
  })

  it('mentions the job page for a submitted review', () => {
    const described = describeReviewOutcome({ outcome: 'submitted', jobId: 'job-1' })
    expect(described.detail).toContain('job page')
  })

  it('passes the backend reason through into the detail', () => {
    const described = describeReviewOutcome({
      outcome: 'notAuthorized',
      reason: 'No webhook covers Prateek2007-cmd/ACM.',
    })
    expect(described.detail).toContain('No webhook covers Prateek2007-cmd/ACM.')
    expect(described.tone).toBe('error')
  })

  it('passes the reason through for a failed submission', () => {
    const described = describeReviewOutcome({
      outcome: 'submissionFailed',
      reason: 'The provider timed out.',
    })
    expect(described.detail).toContain('The provider timed out.')
  })

  it('still gives a useful detail when no reason is present', () => {
    const described = describeReviewOutcome({ outcome: 'submissionFailed' })
    expect(described.detail).toContain('The review could not be submitted.')
  })
})
