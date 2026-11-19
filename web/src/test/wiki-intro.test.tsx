import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { WikiHome } from '../features/wiki/WikiHome'
import type { Folder, Wiki, WikiRun } from '../api'

const folder: Folder = { id: 2, name: 'Orders', parentId: null, path: 'Orders', role: 'owner', shareRootId: 2, shareRootName: 'Orders', canWrite: true, canEdit: true, canManage: true, documentCount: 3, folderCount: 0 }
const failedRun: WikiRun = { id: 5, mode: 'incremental', status: 'failed', stage: 'failed', completed: 0, total: 0, createdUtc: '', startedUtc: null, finishedUtc: null, error: 'The wiki refresh failed. What was already built is kept; refresh again to continue.', stats: null } as unknown as WikiRun
const emptyWiki: Wiki = { id: 7, folderId: 2, folder: 'Orders', language: 'auto', guidelines: null, pages: 0, canRefresh: true, canDelete: true, lastRun: null, refreshedUtc: null }
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
const status = { pages: 0, stalePages: 0, sources: 0, pendingSources: 0, excludedSources: 0 }

function api(wikis: Wiki[]) {
  const fetch = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    if (url === '/api/wikis' && init?.method === 'POST') return Promise.resolve(json(emptyWiki))
    if (url === '/api/wikis') return Promise.resolve(json(wikis))
    if (url.endsWith('/status')) return Promise.resolve(json(status))
    if (url.endsWith('/runs') && init?.method === 'POST') return Promise.resolve(json({ ...failedRun, status: 'queued', stage: 'queued', error: null }))
    // The run never reports back in these tests: the view keeps showing it as queued.
    if (url.endsWith('/events')) return new Promise(() => {})
    return Promise.resolve(new Response(null, { status: 204 }))
  })
  vi.stubGlobal('fetch', fetch)
  return fetch
}

describe('wiki introduction', () => {
  it('explains the wiki when the folder has none, then creates it and starts the first build', async () => {
    const fetch = api([])
    const user = userEvent.setup()
    render(<WikiHome folder={folder} onOpenDocument={() => true} />)

    expect(await screen.findByRole('heading', { name: 'Turn Orders into a living wiki' })).toBeInTheDocument()
    for (const name of ['How it works', 'How to create your wiki', 'Is a wiki right for this folder?', 'Projects that benefit from a wiki', 'When a wiki is not worth it'])
      expect(screen.getByRole('heading', { name })).toBeInTheDocument()
    expect(screen.getByRole('figure', { name: 'How a wiki is built' })).toBeInTheDocument()
    expect(screen.getByRole('figure', { name: 'Documentation linked to the code that implements it' })).toBeInTheDocument()

    await user.type(screen.getByLabelText('Language of the pages'), 'English')
    await user.click(screen.getByRole('button', { name: 'Create and build the wiki' }))

    await waitFor(() => expect(fetch.mock.calls.some(([url, init]) => url === '/api/wikis/7/runs' && init?.method === 'POST')).toBe(true))
    const created = fetch.mock.calls.find(([url, init]) => url === '/api/wikis' && init?.method === 'POST')
    expect(JSON.parse(created?.[1].body)).toMatchObject({ folderId: 2, language: 'English' })
    expect(await screen.findByText(/Refresh: Waiting for the worker/)).toBeInTheDocument()
  })

  it('invites to build a wiki that was never built, without dwelling on an earlier failure', async () => {
    api([{ ...emptyWiki, lastRun: failedRun }])
    render(<WikiHome folder={folder} onOpenDocument={() => true} />)

    expect(await screen.findByRole('button', { name: 'Build the wiki' })).toBeInTheDocument()
    expect(screen.getByText('Your wiki is ready to be built')).toBeInTheDocument()
    expect(screen.queryByText(failedRun.error ?? '')).not.toBeInTheDocument()
    expect(screen.queryByText('There is nothing to draw yet.')).not.toBeInTheDocument()
  })

  it('tells readers who cannot create a wiki who can', async () => {
    api([])
    render(<WikiHome folder={{ ...folder, role: 'read', canWrite: false }} onOpenDocument={() => true} />)

    expect(await screen.findByText(/Someone with write access to the folder can create it/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create and build the wiki' })).not.toBeInTheDocument()
  })
})
