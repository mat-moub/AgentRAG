import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { Explore } from '../features/workspace/Explore'
const passages = [
  { id: 1, documentId: 1, title: 'Guide', index: 0, content: 'Customer onboarding starts here.', x: -1, y: 1, score: .91, exact: true },
  { id: 2, documentId: 1, title: 'Guide', index: 1, content: 'Help new customers get started.', x: 1, y: -1, score: .84, exact: false },
]
describe('Knowledge exploration', () => {
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
