import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { WikiRun } from '../api'
import { WikiRunProgress } from '../features/wiki/WikiRunProgress'
import { estimateWikiStage, serverOffset, wikiRunPercent, wikiStageCount } from '../features/wiki/wikiProgress'

const at = (seconds: number) => new Date(Date.UTC(2026, 10, 21, 10, 0, seconds)).toISOString()
const run = (overrides: Partial<WikiRun> = {}): WikiRun => ({
  id: 3, mode: 'incremental', status: 'running', stage: 'extracting', completed: 0, total: 14,
  createdUtc: at(0), startedUtc: at(0), finishedUtc: null, error: null, stats: null,
  stageStartedUtc: at(10), stageStartCompleted: 0, now: at(10), ...overrides,
})

describe('Wiki run time estimates', () => {
  it('waits for a first item, then projects the pace of the current stage onto what it has left', () => {
    expect(estimateWikiStage(run(), Date.parse(at(40)))).toBeNull()
    // 3 sources in 30 seconds: 11 left take 110 seconds.
    expect(estimateWikiStage(run({ completed: 3 }), Date.parse(at(40)))).toBe(110)
    expect(estimateWikiStage(run({ completed: 14 }), Date.parse(at(40)))).toBeNull()
    expect(estimateWikiStage(run({ completed: 3, stageStartedUtc: null }), Date.parse(at(40)))).toBeNull()
  })

  it('counts only the items done since the stage started, not those done before a restart', () => {
    // 2 of the 5 sources were read before the restart: the pace is 3 sources in 30 seconds.
    expect(estimateWikiStage(run({ completed: 5, stageStartCompleted: 2 }), Date.parse(at(40)))).toBe(90)
  })

  it('measures time on the server clock, whatever the browser clock says', () => {
    const received = Date.parse('2030-01-01T00:00:00Z')
    const offset = serverOffset(run({ now: at(10) }), received)
    expect(received + 5000 + offset).toBe(Date.parse(at(15)))
    expect(serverOffset(run({ now: null }), received)).toBe(0)
  })

  it('names what the count of each stage means', () => {
    expect(wikiStageCount(run({ completed: 3 }))).toBe('3 of 14 sources read')
    expect(wikiStageCount(run({ stage: 'writing', completed: 1, total: 1 }))).toBe('1 of 1 page written')
    expect(wikiStageCount(run({ stage: 'resolving', total: 0 }))).toBeNull()
    expect(wikiRunPercent(run({ completed: 7 }))).toBe(23)
  })
})

describe('Wiki run progress', () => {
  it('shows the stage, its count, the time left, the elapsed time and the steps', () => {
    render(<WikiRunProgress run={run({ completed: 3, now: at(40) })} />)

    expect(screen.getByRole('status')).toHaveTextContent('Refresh: Reading sources')
    expect(screen.getByText('3 of 14 sources read')).toBeInTheDocument()
    expect(screen.getByText('About 2 min left for this step')).toBeInTheDocument()
    expect(screen.getByText('40s elapsed · step 2 of 6')).toBeInTheDocument()
    const steps = screen.getByRole('list', { name: 'Wiki run steps' })
    expect(steps).toHaveTextContent('Listing sources (done)')
    expect(screen.getByText('Reading sources', { selector: '[aria-current="step"]' })).toBeInTheDocument()
  })

  it('counts elapsed time in whole seconds when the clock moves between two readings, as on a slow machine', () => {
    let clock = Date.parse('2030-01-01T00:00:00Z')
    const now = vi.spyOn(Date, 'now').mockImplementation(() => (clock += 7))
    try {
      render(<WikiRunProgress run={run({ completed: 3, now: at(40) })} />)

      expect(screen.getByText('40s elapsed · step 2 of 6')).toBeInTheDocument()
    } finally { now.mockRestore() }
  })

  it('says it is estimating until the stage has finished an item, and gives the time spent in steps without a count', () => {
    const { rerender } = render(<WikiRunProgress run={run({ now: at(20) })} />)
    expect(screen.getByText('Estimating the time left for this step…')).toBeInTheDocument()

    rerender(<WikiRunProgress run={run({ stage: 'resolving', total: 0, stageStartedUtc: at(100), now: at(190) })} />)
    expect(screen.getByText('2 min in this step')).toBeInTheDocument()
  })

  it('explains a queued run without steps', () => {
    render(<WikiRunProgress run={run({ status: 'queued', stage: 'queued', total: 0, startedUtc: null, stageStartedUtc: null })} />)

    expect(screen.getByRole('status')).toHaveTextContent('Refresh: Waiting for the worker')
    expect(screen.getByText('Runs start one at a time.')).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Wiki run steps' })).not.toBeInTheDocument()
  })
})
