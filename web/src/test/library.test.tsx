import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Workspace } from '../features/workspace/Workspace'
import { AddDocument } from '../features/documents/Documents'
import type { Folder, Library } from '../api'

const root: Folder = { id: 2, name: 'Contracts', parentId: null, path: 'Contracts', role: 'owner', shareRootId: 2, shareRootName: 'Contracts', canWrite: true, canEdit: true, canManage: true, documentCount: 2, folderCount: 1 }
const child: Folder = { ...root, id: 3, name: 'Annexes', path: 'Contracts / Annexes', parentId: 2, documentCount: 1, folderCount: 0 }
const documents = [
  { id: 11, title: 'Contract', createdUtc: '', chunks: 3, folderId: 2, canWrite: true },
  { id: 12, title: 'Annex', createdUtc: '', chunks: 2, folderId: 3, canWrite: true },
  { id: 13, title: 'Private notes', createdUtc: '', chunks: 1, folderId: null, canWrite: true },
]
const ownerLibrary: Library = { folders: [root, child], documents, revision: 'initial-access' }
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
const stream = (text: string) => new Response(JSON.stringify({ type: 'delta', text }) + '\n{"type":"completed"}\n', { headers: { 'Content-Type': 'application/x-ndjson' } })
function animationEnd(element: HTMLElement, animationName = 'folder-hide') {
  // jsdom lacks AnimationEvent but exposes WebkitAnimation, so React uses the prefixed event.
  const type = 'AnimationEvent' in window ? 'animationend' : 'webkitAnimationEnd'
  fireEvent(element, Object.assign(new Event(type, { bubbles: true }), { animationName }))
}

function api(initial: Library = ownerLibrary) {
  let library = initial
  const fetch = vi.fn().mockImplementation((url: string) => {
    if (url === '/api/library') return Promise.resolve(json(library))
    if (url.endsWith('/members')) return Promise.resolve(json([]))
    if (url === '/api/explore') return Promise.resolve(json({ total: 0, passages: [] }))
    if (url === '/api/chat/stream') return Promise.resolve(stream('Answer from the selected sources'))
    return Promise.resolve(new Response(null, { status: 204 }))
  })
  vi.stubGlobal('fetch', fetch)
  return { fetch, replace: (next: Library) => { library = next } }
}

describe('Shared folder workflows', () => {
  it('keeps collapsing children mounted until their exit animation ends and blocks interaction immediately', async () => {
    api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    const parent = await screen.findByRole('button', { name: 'Contracts' })
    await user.click(parent)
    const contents = screen.getByRole('list', { name: 'Contracts contents' })
    await user.click(parent)
    expect(parent).toHaveAttribute('aria-expanded', 'false')
    expect(contents).toBeInTheDocument()
    expect(contents).toHaveAttribute('inert')
    expect(contents).toHaveAttribute('aria-hidden', 'true')
    expect(screen.queryByRole('button', { name: 'Contract' })).not.toBeInTheDocument()
    animationEnd(contents, 'folder-reveal')
    expect(contents).toBeInTheDocument()
    animationEnd(contents)
    expect(contents).not.toBeInTheDocument()
  })

  it('allows collapsing a selected descendant’s parent and ignores nested animation completion', async () => {
    api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    const parent = await screen.findByRole('button', { name: 'Contracts' })
    await user.click(parent)
    await user.click(screen.getByRole('button', { name: 'Annexes' }))
    const contents = screen.getByRole('list', { name: 'Contracts contents' })
    const nestedContents = screen.getByRole('list', { name: 'Annexes contents' })
    await user.click(parent)
    expect(parent).toHaveAttribute('aria-expanded', 'false')
    animationEnd(nestedContents)
    expect(contents).toBeInTheDocument()
    animationEnd(contents)
    expect(contents).not.toBeInTheDocument()
  })

  it('keeps reopened children visible if an earlier closing animation completes', async () => {
    api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    const parent = await screen.findByRole('button', { name: 'Contracts' })
    await user.click(parent)
    const contents = screen.getByRole('list', { name: 'Contracts contents' })
    await user.click(parent)
    await user.click(parent)
    animationEnd(contents)
    expect(parent).toHaveAttribute('aria-expanded', 'true')
    expect(contents).not.toHaveAttribute('inert')
    expect(screen.getByRole('button', { name: 'Contract' })).toBeVisible()
  })

  it('does not restore an unfinished nested exit after its parent has collapsed', async () => {
    api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    const parent = await screen.findByRole('button', { name: 'Contracts' })
    await user.click(parent)
    const child = screen.getByRole('button', { name: 'Annexes' })
    await user.click(child)
    await user.click(child)
    const contents = screen.getByRole('list', { name: 'Contracts contents' })
    await user.click(parent)
    animationEnd(contents)
    await user.click(parent)
    expect(screen.getByRole('button', { name: 'Annexes' })).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('list', { name: 'Annexes contents', hidden: true })).not.toBeInTheDocument()
  })

  it('browses folders without changing research sources and shows inherited access', async () => {
    api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Contracts' }))
    await user.click(screen.getByRole('button', { name: 'Annexes' }))
    expect(screen.getByLabelText('Sources')).toHaveTextContent('All accessible sources')
    expect(screen.getByText('Access inherited from Contracts', { exact: false })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Annex' })).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Add a document' }))
    expect(within(screen.getByRole('dialog')).getByLabelText('Destination')).toHaveTextContent('Contracts / Annexes')
    expect(screen.getByText('Everyone with access to this folder can consult the document.')).toBeVisible()
  })

  it('shows a reader their role without mutation controls or member addresses', async () => {
    api({ ...ownerLibrary, folders: [root, child].map(f => ({ ...f, role: 'read', canWrite: false, canEdit: false, canManage: false })), documents: documents.slice(0, 2).map(d => ({ ...d, canWrite: false })) })
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Actions for Contracts' }))
    const dialog = within(screen.getByRole('dialog'))
    expect(dialog.getByText(/Read access/)).toBeVisible()
    expect(dialog.queryByRole('button', { name: 'Rename' })).not.toBeInTheDocument()
    expect(dialog.queryByRole('button', { name: 'Share folder' })).not.toBeInTheDocument()
    expect(dialog.queryByRole('button', { name: 'Delete folder' })).not.toBeInTheDocument()
    expect(dialog.getByRole('button', { name: 'Leave shared folder' })).toBeVisible()
  })

  it('lets writers add content while protecting the shared root', async () => {
    api({ ...ownerLibrary, folders: [{ ...root, role: 'write', canManage: false, canEdit: false }, { ...child, role: 'write', canManage: false }] })
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Actions for Contracts' }))
    const dialog = within(screen.getByRole('dialog'))
    expect(dialog.getByRole('button', { name: 'Add a document' })).toBeVisible()
    expect(dialog.getByRole('button', { name: 'Create subfolder' })).toBeVisible()
    expect(dialog.queryByRole('button', { name: 'Rename' })).not.toBeInTheDocument()
    expect(dialog.queryByRole('button', { name: 'Delete folder' })).not.toBeInTheDocument()
  })

  it('creates a nested folder in the selected parent', async () => {
    const { fetch } = api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Contracts' }))
    await user.click(screen.getByRole('button', { name: 'Create folder' }))
    const dialog = within(screen.getByRole('dialog'))
    expect(dialog.getByText('Location: Contracts')).toBeVisible()
    await user.type(dialog.getByLabelText('Name'), 'New folder')
    await user.click(dialog.getByRole('button', { name: 'Create folder' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    const call = fetch.mock.calls.find(call => call[0] === '/api/library/folders')!
    expect(JSON.parse(call[1].body)).toEqual({ name: 'New folder', parentId: 2 })
  })

  it('grants registered users read access and keeps member management owner-only', async () => {
    const { fetch } = api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Actions for Contracts' }))
    await user.click(screen.getByRole('button', { name: 'Share folder' }))
    const dialog = within(screen.getByRole('dialog'))
    await waitFor(() => expect(dialog.queryByText('Loading members…')).not.toBeInTheDocument())
    await user.type(dialog.getByLabelText("Registered user's email"), 'reader@example.test')
    await user.click(dialog.getByRole('button', { name: 'Give access' }))
    await waitFor(() => expect(fetch.mock.calls.some(call => call[0] === '/api/library/folders/2/members' && call[1]?.method === 'POST')).toBe(true))
    const call = fetch.mock.calls.find(call => call[0] === '/api/library/folders/2/members' && call[1]?.method === 'POST')!
    expect(JSON.parse(call[1].body)).toEqual({ email: 'reader@example.test', role: 'read' })
    expect(dialog.getByText(/including future additions/)).toBeVisible()
  })

  it('requires acknowledging access changes before moving private content into a shared folder', async () => {
    const { fetch } = api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Actions for Private notes' }))
    await user.click(screen.getByRole('button', { name: 'Move to…' }))
    const dialog = within(screen.getByRole('dialog'))
    await user.click(dialog.getByLabelText('Destination folder'))
    await user.click(screen.getByRole('option', { name: 'Contracts' }))
    expect(dialog.getByRole('button', { name: 'Move here' })).toBeDisabled()
    await user.click(dialog.getByRole('checkbox'))
    await user.click(dialog.getByRole('button', { name: 'Move here' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    const call = fetch.mock.calls.find(call => call[0] === '/api/documents/13/move')!
    expect(JSON.parse(call[1].body)).toEqual({ folderId: 2, confirmAccessChange: true })
  })

  it('starts a fresh conversation for a new scope and passes that scope to Explore', async () => {
    const { fetch } = api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    const input = await screen.findByRole('textbox', { name: 'Ask a question about your documents' })
    await waitFor(() => expect(input).toBeEnabled())
    await user.type(input, 'First question')
    await user.click(screen.getByRole('button', { name: 'Send question' }))
    await screen.findByLabelText('AgentRAG answer')
    await user.click(screen.getByLabelText('Sources'))
    await user.click(screen.getByRole('option', { name: 'Contracts / Annexes' }))
    expect(screen.queryByLabelText('AgentRAG answer')).not.toBeInTheDocument()
    await user.type(screen.getByRole('textbox', { name: 'Ask a question about your documents' }), 'Scoped question')
    await user.click(screen.getByRole('button', { name: 'Send question' }))
    await screen.findByLabelText('AgentRAG answer')
    const calls = fetch.mock.calls.filter(call => call[0] === '/api/chat/stream')
    expect(JSON.parse(calls[1][1].body)).toMatchObject({ folderId: 3, history: [], libraryRevision: 'initial-access' })
    await user.click(screen.getByRole('button', { name: 'Explore documents' }))
    await waitFor(() => expect(fetch.mock.calls.some(call => call[0] === '/api/explore')).toBe(true))
    expect(JSON.parse(fetch.mock.calls.find(call => call[0] === '/api/explore')![1].body)).toMatchObject({ folderId: 3 })
    await user.click(screen.getByLabelText('Filter by document'))
    expect(screen.queryByRole('option', { name: 'Private notes' })).not.toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Annex' })).toBeInTheDocument()
  })

  it('opens a sidebar document directly in its passage view', async () => {
    const { fetch } = api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await user.click(await screen.findByRole('button', { name: 'Private notes' }))
    await waitFor(() => expect(fetch.mock.calls.some(call => call[0] === '/api/explore')).toBe(true))
    expect(screen.getByLabelText('Filter by document')).toHaveTextContent('Private notes')
    expect(JSON.parse(fetch.mock.calls.find(call => call[0] === '/api/explore')![1].body)).toMatchObject({ documentId: 13 })
  })

  it('aborts an in-flight answer and clears old context when access is revoked', async () => {
    const mock = api()
    let signal: AbortSignal | undefined
    mock.fetch.mockImplementation((url: string, init?: RequestInit) => {
      if (url === '/api/chat/stream') { signal = init?.signal ?? undefined; return new Promise<Response>(() => {}) }
      return Promise.resolve(json(ownerLibrary))
    })
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Ask a question about your documents' })).toBeEnabled())
    await user.type(screen.getByRole('textbox', { name: 'Ask a question about your documents' }), 'Sensitive question')
    await user.click(screen.getByRole('button', { name: 'Send question' }))
    await act(async () => window.dispatchEvent(new Event('agentrag:access-changed')))
    expect(signal?.aborted).toBe(true)
    expect(screen.queryByLabelText('Your question')).not.toBeInTheDocument()
  })

  it('does not broaden a revoked folder scope to remaining sources', async () => {
    const mock = api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await screen.findByRole('button', { name: 'Contracts' })
    await user.click(screen.getByLabelText('Sources'))
    await user.click(screen.getByRole('option', { name: 'Contracts' }))
    mock.replace({ folders: [], documents: documents.slice(2), revision: 'revoked' })
    await act(async () => window.dispatchEvent(new Event('agentrag:access-changed')))
    await screen.findByText('This folder was removed or is no longer shared with you.')
    expect(screen.queryByRole('textbox', { name: 'Ask a question about your documents' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Sources')).toHaveTextContent('Folder no longer available')
    await user.click(screen.getByLabelText('Sources'))
    await user.click(screen.getByRole('option', { name: 'All accessible sources' }))
    expect(screen.getByRole('textbox', { name: 'Ask a question about your documents' })).toBeEnabled()
  })

  it('opens the mobile library and dismisses it using Escape', async () => {
    api()
    render(<Workspace onLogout={() => {}} />)
    await screen.findByRole('button', { name: 'Contracts' })
    fireEvent.click(screen.getByRole('button', { name: 'Open documents' }))
    const dialog = screen.getByRole('dialog')
    expect(within(dialog).getByRole('navigation', { name: 'Document library' })).toBeVisible()
    fireEvent(dialog, new Event('cancel', { cancelable: true }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('restores focus to the mobile launcher after leaving a folder action dialog', async () => {
    api()
    const user = userEvent.setup()
    render(<Workspace onLogout={() => {}} />)
    await screen.findByRole('button', { name: 'Contracts' })
    const launcher = screen.getByRole('button', { name: 'Open documents' })
    await user.click(launcher)
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Actions for Contracts' }))
    await user.click(screen.getByRole('button', { name: 'Close dialog' }))
    await waitFor(() => expect(launcher).toHaveFocus())
  })

  it('requires a new import destination when write access disappears', async () => {
    const user = userEvent.setup()
    const onAdd = vi.fn()
    const { rerender } = render(<AddDocument onClose={() => {}} onAdd={onAdd} folders={[root]} initialFolderId={2} />)
    await user.type(screen.getByLabelText('Document title'), 'Draft')
    await user.type(screen.getByLabelText('Content'), 'Text')
    expect(screen.getByRole('button', { name: 'Add document' })).toBeEnabled()
    rerender(<AddDocument onClose={() => {}} onAdd={onAdd} folders={[]} initialFolderId={2} />)
    expect(screen.getByRole('button', { name: 'Add document' })).toBeDisabled()
    expect(screen.getByText('Your access changed. Choose a new destination to continue.')).toBeVisible()
    await user.click(screen.getByLabelText('Destination'))
    await user.click(screen.getByRole('option', { name: 'Unfiled documents' }))
    expect(screen.getByRole('button', { name: 'Add document' })).toBeEnabled()
    expect(onAdd).not.toHaveBeenCalled()
  })
})
