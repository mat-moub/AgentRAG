import { FilesIcon, FolderIcon, LockSimpleIcon, UsersIcon } from '@phosphor-icons/react'
import type { Folder } from '../api'
import { Select } from './Select'
import type { SelectOption } from './Select'

export function FolderSelect({ folders, value, onValueChange, label, id, disabled, className, autoFocus,
  includeRoot = true, rootLabel = 'Unfiled documents', rootDescription = 'Private · Only you', unavailableLabel = 'Folder no longer available',
  'aria-describedby': describedBy }: {
  folders: Folder[]; value: number | null; onValueChange: (value: number | null) => void; label: string
  id?: string; disabled?: boolean; className?: string; autoFocus?: boolean; includeRoot?: boolean
  rootLabel?: string; rootDescription?: string; unavailableLabel?: string; 'aria-describedby'?: string
}) {
  const options: SelectOption[] = includeRoot ? [{ value: '', label: rootLabel, description: rootDescription, icon: <FilesIcon size={19} /> }] : []
  if (value !== null && !folders.some(folder => folder.id === value))
    options.push({ value: String(value), label: unavailableLabel, description: 'Choose an available destination.', disabled: true, icon: <LockSimpleIcon size={19} /> })
  for (const folder of [...folders].sort((a, b) => a.path.localeCompare(b.path, undefined, { numeric: true }))) {
    const parents = folder.path.split(' / ').slice(0, -1)
    options.push({
      value: String(folder.id), label: folder.name, selectedLabel: folder.path,
      description: [...parents, folder.role === 'read' ? 'Read access' : folder.role === 'write' ? 'Write access' : 'Owner'].join(' / '),
      keywords: `${folder.path} ${folder.shareRootId ? 'shared' : 'private'}`,
      icon: folder.shareRootId ? <UsersIcon size={19} /> : <FolderIcon size={19} />,
      meta: folder.shareRootId ? 'Shared' : 'Private', depth: parents.length,
    })
  }
  return <Select id={id} label={label} value={value === null ? '' : String(value)} options={options} disabled={disabled} className={className}
    autoFocus={autoFocus} searchable searchPlaceholder="Search folders…" emptyMessage="No matching folders." aria-describedby={describedBy}
    onValueChange={next => onValueChange(next === '' ? null : Number(next))} />
}
