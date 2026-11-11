import { useState } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Dialog, Select } from '../components/ui'
import type { SelectOption } from '../components/ui'
import { FolderSelect } from '../components/FolderSelect'
import type { Folder } from '../api'

const options: SelectOption[] = [
  { value: 'private', label: 'Private documents' },
  { value: 'unavailable', label: 'Unavailable', disabled: true },
  { value: 'contracts', label: 'Contracts', description: 'Project Atlas', keywords: 'shared' },
  { value: 'notes', label: 'Résumé notes' },
]
const folders: Folder[] = [
  { id: 1, name: 'Project Atlas', parentId: null, path: 'Project Atlas', role: 'owner', shareRootId: null, shareRootName: null, canWrite: true, canEdit: true, canManage: true, documentCount: 0, folderCount: 1 },
  { id: 2, name: 'Contracts', parentId: 1, path: 'Project Atlas / Contracts', role: 'write', shareRootId: 2, shareRootName: 'Contracts', canWrite: true, canEdit: false, canManage: false, documentCount: 0, folderCount: 0 },
]

function Controlled({ searchable = false, onChange = () => {} }: { searchable?: boolean; onChange?: (value: string) => void }) {
  const [value, setValue] = useState('private')
  return <Select label="Destination" name="destination" value={value} options={options} searchable={searchable} onValueChange={next => { setValue(next); onChange(next) }} />
}

describe('AgentRAG Select', () => {
  it('opens the list, exposes selection and commits a clicked option without submitting its form', async () => {
    const user = userEvent.setup()
    const submitted = vi.fn(e => e.preventDefault())
    const changed = vi.fn()
    render(<form onSubmit={submitted} aria-label="Import"><Controlled onChange={changed} /></form>)
    const trigger = screen.getByRole('combobox', { name: 'Destination' })
    expect(trigger).toHaveTextContent('Private documents')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    await user.click(trigger)
    expect(trigger).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('option', { name: 'Private documents' })).toHaveAttribute('aria-selected', 'true')
    await user.click(screen.getByRole('option', { name: 'Contracts' }))
    expect(trigger).toHaveTextContent('Contracts')
    expect(trigger).toHaveAttribute('aria-expanded', 'false')
    expect(trigger).toHaveFocus()
    expect(changed).toHaveBeenCalledExactlyOnceWith('contracts')
    expect(submitted).not.toHaveBeenCalled()
    expect(new FormData(screen.getByRole('form') as HTMLFormElement).get('destination')).toBe('contracts')
  })

  it('navigates enabled options with arrows and Home/End without committing until Enter', async () => {
    const user = userEvent.setup()
    const changed = vi.fn()
    render(<Controlled onChange={changed} />)
    const trigger = screen.getByRole('combobox')
    trigger.focus()
    await user.keyboard('{ArrowDown}{ArrowDown}')
    expect(trigger).toHaveAttribute('aria-activedescendant', screen.getByRole('option', { name: 'Contracts' }).id)
    expect(changed).not.toHaveBeenCalled()
    await user.keyboard('{End}')
    expect(trigger).toHaveAttribute('aria-activedescendant', screen.getByRole('option', { name: 'Résumé notes' }).id)
    await user.keyboard('{Home}{ArrowUp}{Enter}')
    expect(trigger).toHaveTextContent('Résumé notes')
    expect(changed).toHaveBeenCalledExactlyOnceWith('notes')
  })

  it('searches labels and context without accents, keeps Space for typing and selects with Enter', async () => {
    const user = userEvent.setup()
    const changed = vi.fn()
    render(<Controlled searchable onChange={changed} />)
    await user.click(screen.getByRole('combobox'))
    const search = screen.getByRole('searchbox')
    expect(search).toHaveFocus()
    await user.type(search, 'resume notes')
    expect(screen.getAllByRole('option')).toHaveLength(1)
    expect(screen.getByRole('option', { name: 'Résumé notes' })).toBeVisible()
    await user.keyboard('{Enter}')
    expect(changed).toHaveBeenCalledExactlyOnceWith('notes')
    expect(screen.getByRole('combobox')).toHaveFocus()
    await user.click(screen.getByRole('combobox'))
    await user.type(screen.getByRole('searchbox'), 'atlas shared')
    expect(screen.getAllByRole('option')).toHaveLength(1)
    expect(screen.getByRole('option', { name: 'Contracts' })).toBeVisible()
  })

  it('shows an empty search state and never selects a disabled option', async () => {
    const user = userEvent.setup()
    const changed = vi.fn()
    render(<Controlled searchable onChange={changed} />)
    await user.click(screen.getByRole('combobox'))
    await user.click(screen.getByRole('option', { name: 'Unavailable' }))
    expect(changed).not.toHaveBeenCalled()
    await user.type(screen.getByRole('searchbox'), 'nothing matches')
    expect(screen.getByRole('status')).toHaveTextContent('No matching options.')
    await user.keyboard('{Enter}')
    expect(changed).not.toHaveBeenCalled()
  })

  it('closes with Escape without dismissing its containing dialog', async () => {
    const user = userEvent.setup()
    const closed = vi.fn()
    render(<Dialog title="Move document" onClose={closed}><Controlled searchable /></Dialog>)
    await user.click(screen.getByRole('combobox'))
    await user.keyboard('{Escape}')
    expect(screen.getByRole('combobox')).toHaveFocus()
    expect(screen.getByRole('dialog', { name: 'Move document' })).toBeVisible()
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(closed).not.toHaveBeenCalled()
  })

  it('dismisses on outside pointer interaction without taking focus from the clicked control', async () => {
    const user = userEvent.setup()
    render(<><Controlled searchable /><button>Continue</button></>)
    await user.click(screen.getByRole('combobox'))
    await user.click(screen.getByRole('button', { name: 'Continue' }))
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Continue' })).toHaveFocus()
  })

  it('supports Tab dismissal and typeahead for short non-searchable lists', async () => {
    const user = userEvent.setup()
    render(<><Controlled /><button>Next field</button></>)
    screen.getByRole('combobox').focus()
    await user.keyboard('c{Enter}')
    expect(screen.getByRole('combobox')).toHaveTextContent('Contracts')
    await user.keyboard(' {Tab}')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Next field' })).toHaveFocus()
  })

  it('respects disabled state and reflects externally changed values', async () => {
    const user = userEvent.setup()
    const changed = vi.fn()
    const { rerender } = render(<Select label="Access" value="private" options={options} disabled onValueChange={changed} />)
    await user.click(screen.getByRole('combobox'))
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    rerender(<Select label="Access" value="contracts" options={options} onValueChange={changed} />)
    expect(screen.getByRole('combobox')).toHaveTextContent('Contracts')
    expect(changed).not.toHaveBeenCalled()
    await user.click(screen.getByRole('combobox'))
    expect(screen.getByRole('listbox')).toBeVisible()
    rerender(<Select label="Access" value="contracts" options={options} disabled onValueChange={changed} />)
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    rerender(<Select label="Access" value="contracts" options={options} onValueChange={changed} />)
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('keeps the popup within a narrow viewport and positions it above a low trigger', async () => {
    const user = userEvent.setup()
    render(<Controlled searchable />)
    const trigger = screen.getByRole('combobox')
    vi.spyOn(trigger, 'getBoundingClientRect').mockReturnValue({ top: 700, bottom: 748, left: 300, right: 500, width: 200, height: 48, x: 300, y: 700, toJSON: () => ({}) })
    const previousWidth = window.innerWidth
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 390 })
    await user.click(trigger)
    const panel = screen.getByRole('listbox').parentElement!
    expect(panel.style.width).toBe('300px')
    expect(panel.style.left).toBe('78px')
    expect(panel.style.transform).toBe('translateY(-100%)')
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: previousWidth })
    fireEvent.resize(window)
  })

  it('presents folder hierarchy, access and full selected paths and supports an unavailable destination', async () => {
    const user = userEvent.setup()
    const changed = vi.fn()
    const { rerender } = render(<FolderSelect label="Destination" value={2} folders={folders} onValueChange={changed} />)
    const trigger = screen.getByRole('combobox')
    expect(trigger).toHaveTextContent('Project Atlas / Contracts')
    await user.click(trigger)
    const shared = screen.getByRole('option', { name: 'Project Atlas / Contracts' })
    expect(within(shared).getByText('Shared')).toBeVisible()
    expect(within(shared).getByText('Project Atlas / Write access')).toBeVisible()
    await user.click(screen.getByRole('option', { name: 'Unfiled documents' }))
    expect(changed).toHaveBeenCalledWith(null)
    rerender(<FolderSelect label="Destination" value={2} folders={[]} onValueChange={changed} />)
    expect(trigger).toHaveTextContent('Folder no longer available')
    await user.click(trigger)
    expect(screen.getByRole('option', { name: 'Folder no longer available' })).toHaveAttribute('aria-disabled', 'true')
    await user.type(screen.getByRole('searchbox'), 'xyz')
    await waitFor(() => expect(screen.getByText('No matching folders.')).toBeVisible())
  })
})
