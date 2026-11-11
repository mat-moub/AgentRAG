import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { StrictMode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { Explore } from '../features/workspace/Explore'
import { Workspace } from '../features/workspace/Workspace'
const passages = [
  { id: 1, documentId: 1, title: 'Guide', index: 0, content: 'Customer onboarding starts here.', x: -1, y: 1, score: .91, exact: true },
  { id: 2, documentId: 1, title: 'Guide', index: 1, content: 'Help new customers get started.', x: 1, y: -1, score: .84, exact: false },
]
describe('Knowledge exploration', () => {
  it('collapses the map and excerpt independently and reopens the excerpt when selecting a passage', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ total: 2, passages }))))
    render(<Explore documents={[]} />)
    const mapToggle = await screen.findByRole('button', { name: 'Toggle passage map' })
    const excerptToggle = screen.getByRole('button', { name: 'Toggle selected excerpt' })
    fireEvent.click(mapToggle)
    expect(mapToggle).toHaveAttribute('aria-expanded', 'false')
    expect(excerptToggle).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(excerptToggle)
    expect(excerptToggle).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(within(screen.getByRole('region', { name: 'Passage results' })).getByRole('button', { name: /Passage 2/ }))
    expect(excerptToggle).toHaveAttribute('aria-expanded', 'true')
    expect(mapToggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByRole('region', { name: 'Selected excerpt content' })).toHaveTextContent('Help new customers get started.')
    fireEvent.click(mapToggle)
    expect(mapToggle).toHaveAttribute('aria-expanded', 'true')
  })

  it('places the map before the results in the reading order', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ total: 2, passages }))))
    render(<Explore documents={[]} />)
    const map = await screen.findByRole('heading', { name: 'Passage map' })
    const results = screen.getByRole('heading', { name: 'Indexed passages' })
    expect(map.compareDocumentPosition(results) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('explains that a limited sample is distributed across documents', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ total: 1000, passages }))))
    render(<Explore documents={[]} />)
    expect(await screen.findByText(/Showing a sample of 2 of 1000 passages, distributed across documents/)).toBeVisible()
  })

  it('colors documents consistently and applies search scores without hiding the map', async () => {
    const points = Array.from({ length: 9 }, (_, index) => ({
      id: index + 1, documentId: index + 1, title: `Document ${index + 1}`, index: 0,
      content: 'A passage', x: index, y: index, score: null, exact: false,
    }))
    let finishSearch!: (response: Response) => void
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ total: points.length, passages: points })))
      .mockImplementationOnce(() => new Promise<Response>(resolve => { finishSearch = resolve }))
    vi.stubGlobal('fetch', fetch)
    render(<Explore documents={[]} />)
    const map = await screen.findByRole('group', { name: 'Two-dimensional passage map' })
    const initialPoint = within(map).getByRole('button', { name: /Document 1, passage 1/ })
    const firstDot = initialPoint.querySelector('span span') as HTMLElement
    const ninthDot = within(map).getByRole('button', { name: /Document 9, passage 1/ }).querySelector('span span') as HTMLElement
    expect(firstDot.style.backgroundColor).toBe(ninthDot.style.backgroundColor)
    expect(firstDot.style.opacity).toBe('1')
    expect(screen.getByText('Colors repeat for some documents.')).toBeVisible()
    expect(screen.getByLabelText('Map legend')).toHaveTextContent('Document 9')

    fireEvent.change(screen.getByLabelText('Search passages'), { target: { value: 'question' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))
    expect(screen.getByRole('status')).toHaveTextContent('Calculating similarity')
    expect(map).toBeVisible()
    expect(firstDot.style.opacity).toBe('1')
    const scores = [-0.4, 0, 0.5, 1, 2, null, 0.1, 0.8, 0.9]
    await act(async () => finishSearch(new Response(JSON.stringify({ total: points.length, passages: points.map((point, index) => ({ ...point, score: scores[index], exact: index === 0 })) }))))
    const firstMatch = within(map).getByRole('button', { name: /Document 1, passage 1, Similarity -0.40, exact phrase match/ })
    expect((firstMatch.querySelector('span span') as HTMLElement).style.opacity).toBe('0.35')
    expect((within(map).getByRole('button', { name: /Document 3, passage 1/ }).querySelector('span span') as HTMLElement).style.opacity).toBe('0.675')
    expect((within(map).getByRole('button', { name: /Document 4, passage 1/ }).querySelector('span span') as HTMLElement).style.opacity).toBe('1')
    expect((within(map).getByRole('button', { name: /Document 6, passage 1/ }).querySelector('span span') as HTMLElement).style.opacity).toBe('1')
    firstMatch.focus()
    expect(firstMatch).toHaveFocus()
    fireEvent.click(firstMatch)
    expect(firstMatch).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByText('Document 1 · Passage 1 · Similarity -0.40')).toBeVisible()
  })

  it('ignores an earlier response after changing the document filter', async () => {
    let finishOld!: (response: Response) => void
    let oldSignal: AbortSignal | undefined
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ total: 2, passages })))
      .mockImplementationOnce((_: string, options: RequestInit) => { oldSignal = options.signal ?? undefined; return new Promise<Response>(resolve => { finishOld = resolve }) })
      .mockResolvedValueOnce(new Response(JSON.stringify({ total: 0, passages: [] })))
    vi.stubGlobal('fetch', fetch)
    render(<Explore documents={[{ id: 1, title: 'Guide', createdUtc: '', chunks: 2 }]} />)
    await screen.findByText('Indexed passages')
    fireEvent.change(screen.getByLabelText('Search passages'), { target: { value: 'old' } })
    fireEvent.click(screen.getByRole('button', { name: 'Search' }))
    fireEvent.click(screen.getByLabelText('Filter by document'))
    fireEvent.click(screen.getByRole('option', { name: 'Guide' }))
    expect(oldSignal?.aborted).toBe(true)
    await screen.findByText('No indexed passages yet')
    await act(async () => finishOld(new Response(JSON.stringify({ total: 2, passages }))))
    expect(screen.queryByRole('group', { name: 'Two-dimensional passage map' })).not.toBeInTheDocument()
  })

  it('keeps the initial request alive when switching views in Strict Mode', async () => {
    const fetch = vi.fn().mockImplementation((url: string) => url === '/api/library'
      ? Promise.resolve(new Response(JSON.stringify({ folders: [], documents: [], revision: 'empty' })))
      : new Promise<Response>(() => {}))
    vi.stubGlobal('fetch', fetch)
    render(<StrictMode><Workspace onLogout={() => {}} /></StrictMode>)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Add your first document' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Explore documents' }))
    await waitFor(() => expect(fetch.mock.calls.filter(call => call[0] === '/api/explore')).toHaveLength(1))
    const request = fetch.mock.calls.find(call => call[0] === '/api/explore')![1] as RequestInit
    fireEvent.click(screen.getByRole('button', { name: 'Conversation' }))
    expect(request.signal?.aborted).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Explore documents' }))
    expect(fetch.mock.calls.filter(call => call[0] === '/api/explore')).toHaveLength(1)
  })

  it('searches, distinguishes exact phrases and synchronizes points with excerpts', async () => {
    const fetch = vi.fn().mockImplementation(() => Promise.resolve(new Response(JSON.stringify({total: 2, passages}))))
    vi.stubGlobal('fetch', fetch)
    render(<Explore documents={[]} />)
    await screen.findByText('Indexed passages')
    fireEvent.change(screen.getByLabelText('Search passages'), {target: {value: 'customer onboarding'}})
    fireEvent.click(screen.getByRole('button', {name: /^Search$/}))
    await screen.findByText('Results for “customer onboarding”')
    expect(screen.getAllByText('Exact phrase').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Semantic similarity').length).toBeGreaterThan(0)
    fireEvent.click(screen.getByRole('button', {name: 'Guide, passage 2, Similarity 0.84'}))
    expect(screen.getByText('Guide · Passage 2 · Similarity 0.84')).toBeInTheDocument()
    expect(JSON.parse(fetch.mock.calls[1][1].body).query).toBe('customer onboarding')
    await waitFor(() => expect(screen.getByText('Selected excerpt')).toBeVisible())
  })
})
