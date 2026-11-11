import { useEffect, useState } from 'react'
import { ArrowBendUpRightIcon, ArrowClockwiseIcon, CaretRightIcon, DotsThreeIcon, FileTextIcon, FolderIcon, FolderPlusIcon, LinkIcon, PencilSimpleIcon, PlusIcon, SignOutIcon, TrashIcon, UsersIcon } from '@phosphor-icons/react'
import { Button, Dialog, Field, IconButton, Notice, Select } from '../../components/ui'
import { FolderSelect } from '../../components/FolderSelect'
import * as api from '../../api'
import type { LibraryState } from './useLibrary'

export type LibraryAction = {
  kind: 'folder' | 'document' | 'create' | 'rename' | 'move' | 'share' | 'delete' | 'leave' | 'stop'
  folder?: api.Folder
  document?: api.Doc
  parentId?: number | null
}

export function LibraryTree({ state, selectedId, onSelect, onAction, onAdd, onOpen }: {
  state: LibraryState; selectedId: number | null; onSelect: (id: number | null) => void
  onAction: (action: LibraryAction) => void; onAdd: (id: number | null) => void; onOpen: (document: api.Doc) => void
}) {
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set(selectedId ? [selectedId] : []))
  const [closing, setClosing] = useState<Set<number>>(() => new Set())
  const ancestors = new Set<number>()
  let ancestor = state.folders.find(f => f.id === selectedId)
  while (ancestor && !ancestors.has(ancestor.id)) { ancestors.add(ancestor.id); ancestor = state.folders.find(f => f.id === ancestor?.parentId) }
  function documentRow(document: api.Doc, depth: number) {
    return <li key={`document-${document.id}`}>
      <div className="flex min-w-0 items-center" style={{ paddingLeft: `${Math.min(depth, 3) * 12}px` }}>
        <button type="button" className="flex min-h-11 min-w-0 flex-1 items-center gap-2 rounded-control px-1 text-left text-sm hover:bg-subtle" title={`${document.title} · ${document.chunks} passages`} onClick={() => onOpen(document)}><FileTextIcon size={18} className="shrink-0 text-muted" aria-hidden /><span className="min-w-0 truncate">{document.title}</span></button>
        <IconButton label={`Actions for ${document.title}`} onClick={() => onAction({ kind: 'document', document })}><DotsThreeIcon size={21} /></IconButton>
      </div>
    </li>
  }
  function folderRow(folder: api.Folder, depth: number): React.ReactNode {
    const open = expanded.has(folder.id) || ancestors.has(folder.id)
    const children = state.folders.filter(f => f.parentId === folder.id)
    const documents = state.documents.filter(d => d.folderId === folder.id)
    return <li key={`folder-${folder.id}`}>
      <div className={`library-folder-row flex min-w-0 items-center rounded-control ${selectedId === folder.id ? 'bg-primary/8 text-ink' : ''}`} style={{ paddingLeft: `${Math.min(depth, 3) * 12}px` }}>
        <button type="button" className="flex min-h-11 min-w-0 flex-1 items-center gap-1.5 rounded-control text-left text-sm" aria-label={folder.name} aria-expanded={open} title={`${folder.path} · ${folder.role === 'read' ? 'Read access' : folder.role === 'write' ? 'Write access' : 'Owner'}${folder.shareRootId ? ' · Shared' : ''}`} onClick={() => {
          const next = new Set(expanded)
          if (open) {
            setClosing(current => new Set(current).add(folder.id))
            next.delete(folder.id)
            if (ancestors.has(folder.id)) onSelect(null)
          } else {
            setClosing(current => { const updated = new Set(current); updated.delete(folder.id); return updated })
            next.add(folder.id)
            onSelect(folder.id)
          }
          setExpanded(next)
        }}><CaretRightIcon size={13} aria-hidden className={`shrink-0 transition-transform ${open ? 'rotate-90' : ''}`} /><FolderIcon size={19} aria-hidden className={`shrink-0 ${selectedId === folder.id ? 'text-accent' : 'text-muted'}`} /><span className="min-w-0 truncate">{folder.name}</span>{folder.shareRootId === folder.id && <UsersIcon size={14} className="shrink-0 text-muted" aria-label="Shared folder" />}</button>
        <IconButton label={`Actions for ${folder.name}`} className="hover:bg-transparent hover:text-accent" onClick={() => onAction({ kind: 'folder', folder })}><DotsThreeIcon size={21} /></IconButton>
      </div>
      {(open || closing.has(folder.id)) && <ul className={`library-folder-contents${open ? '' : ' library-folder-contents-closing'}`} aria-label={`${folder.name} contents`} aria-hidden={!open || undefined} inert={!open} onAnimationEnd={event => {
        if (open || event.target !== event.currentTarget || event.animationName !== 'folder-hide') return
        setClosing(current => {
          const updated = new Set(current)
          const pending = [folder.id]
          while (pending.length) {
            const id = pending.pop()!
            updated.delete(id)
            pending.push(...state.folders.filter(child => child.parentId === id).map(child => child.id))
          }
          return updated
        })
      }}>{children.map(child => folderRow(child, depth + 1))}{documents.map(d => documentRow(d, depth + 1))}{children.length + documents.length === 0 && <li className="py-2 text-xs text-muted" style={{ paddingLeft: `${Math.min(depth + 1, 3) * 12 + 20}px` }}>Empty folder</li>}</ul>}
    </li>
  }
  const mine = state.folders.filter(f => f.parentId === null && f.role === 'owner')
  const shared = state.folders.filter(f => f.parentId === null && f.role !== 'owner')
  const selected = state.folders.find(f => f.id === selectedId)
  return <nav aria-label="Document library" className="pb-5">
    <div className="flex items-center justify-between gap-1 pb-3"><h2 className="text-sm font-semibold">Your library</h2><div className="flex"><IconButton label="Refresh library" disabled={state.loading} onClick={() => void state.refresh()}><ArrowClockwiseIcon size={17} /></IconButton><IconButton label="Create folder" disabled={state.loading || !!state.error} onClick={() => onAction({ kind: 'create', parentId: selected?.canWrite ? selected.id : null })}><FolderPlusIcon size={19} /></IconButton></div></div>
    {state.error && <Notice>{state.error}</Notice>}
    {!state.loading && !state.error && selectedId !== null && !selected && <Notice>This folder is unavailable or you no longer have access.</Notice>}
    {state.loading ? <p role="status" className="py-4 text-sm text-muted">Loading library…</p> : !state.error && <>
      <details open><summary className="flex min-h-11 cursor-pointer items-center text-[11px] font-medium tracking-widest text-muted uppercase">My folders <span className="ml-2 tracking-normal">{mine.length}</span></summary><ul>{mine.map(f => folderRow(f, 0))}</ul>{!mine.length && <p className="pb-3 text-xs leading-5 text-muted">Create a folder to organize your research.</p>}</details>
      <details open className="mt-2 border-t border-line pt-2"><summary className="min-h-11 cursor-pointer py-3 text-[11px] font-medium tracking-widest text-muted uppercase">Unfiled documents</summary><ul>{state.documents.filter(d => d.folderId == null).map(d => documentRow(d, 0))}</ul>{!state.documents.some(d => d.folderId == null) && <p className="pb-3 text-xs text-muted">No unfiled documents.</p>}</details>
      <details open className="mt-2 border-t border-line pt-2"><summary className="min-h-11 cursor-pointer py-3 text-[11px] font-medium tracking-widest text-muted uppercase">Shared with me <span className="ml-1 tracking-normal">{shared.length}</span></summary><ul>{shared.map(f => folderRow(f, 0))}</ul>{!shared.length && <p className="pb-3 text-xs leading-5 text-muted">Folders shared with you will appear here.</p>}</details>
    </>}
    {selected && <p className="mt-3 break-words text-xs leading-5 text-muted">{selected.canWrite ? 'Adding to:' : 'Selected folder:'} {selected.path}<br />{selected.role === 'read' ? 'Read access' : selected.role === 'write' ? 'Write access' : selected.shareRootId ? 'Shared · Owner' : 'Private · Owner'}{selected.shareRootId !== null && selected.shareRootId !== selected.id && <><br />Access inherited from {selected.shareRootName}</>}</p>}
    <Button variant="ghost" className="mt-4 w-full justify-start rounded-none border-0 border-t border-line px-0" disabled={state.loading || !!state.error} onClick={() => onAdd(selected?.canWrite ? selected.id : null)}><PlusIcon size={19} />{selected && !selected.canWrite ? 'Add a private document' : 'Add a document'}</Button>
  </nav>
}

export function LibraryManager({ action, state, onAction, onClose, onAdd }: {
  action: LibraryAction; state: LibraryState; onAction: (action: LibraryAction) => void
  onClose: () => void; onAdd: (id: number | null) => void
}) {
  const folder = state.folders.find(f => f.id === action.folder?.id)
  const document = state.documents.find(d => d.id === action.document?.id)
  const title = folder?.name ?? document?.title ?? ''
  const [name, setName] = useState(title)
  const [destination, setDestination] = useState<string>(String(folder?.parentId ?? document?.folderId ?? ''))
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [copied, setCopied] = useState(false)
  const [members, setMembers] = useState<api.FolderMember[]>([])
  const [membersLoading, setMembersLoading] = useState(action.kind === 'share')
  const [email, setEmail] = useState('')
  const [role, setRole] = useState('read')
  const sharingFolderId = action.kind === 'share' ? folder?.id : undefined
  useEffect(() => {
    if (!sharingFolderId) return
    const controller = new AbortController()
    api.listMembers(sharingFolderId, controller.signal).then(result => { if (!controller.signal.aborted) setMembers(result) }).catch(e => { if (!controller.signal.aborted) setError(api.errorMessage(e)) }).finally(() => { if (!controller.signal.aborted) setMembersLoading(false) })
    return () => controller.abort()
  }, [sharingFolderId])

  async function run(operation: () => Promise<unknown>, keepOpen = false) {
    if (busy) return
    setBusy(true); setError('')
    try {
      await operation()
      await state.refresh()
      if (keepOpen && folder) { setMembers(await api.listMembers(folder.id)); setEmail('') }
      else onClose()
    } catch (e) { setError(api.errorMessage(e)) }
    finally { setBusy(false) }
  }
  const close = () => { if (!busy) onClose() }
  const change = (kind: LibraryAction['kind']) => onAction({ ...action, kind })
  if ((action.folder && !folder) || (action.document && !document)) return <Dialog title="Item unavailable" onClose={onClose}><p className="text-sm leading-6 text-muted">This item was removed or your access changed.</p><Button className="mt-5" onClick={onClose}>Close</Button></Dialog>
  const kind = folder ? 'folders' : 'documents'
  const id = folder?.id ?? document?.id ?? 0
  const source = folder ?? state.folders.find(f => f.id === document?.folderId)
  const owner = !source || source.role === 'owner'
  const sourceRoot = source?.shareRootId ?? null
  const target = state.folders.find(f => f.id === Number(destination))
  const targetRoot = folder && folder.shareRootId === folder.id ? folder.id : target?.shareRootId ?? null
  const accessChanges = sourceRoot !== targetRoot
  function descendant(candidate: api.Folder) {
    const visited = new Set<number>()
    let current: api.Folder | undefined = candidate
    while (current && !visited.has(current.id)) {
      if (current.id === folder?.id) return true
      visited.add(current.id)
      current = state.folders.find(f => f.id === current?.parentId)
    }
    return false
  }
  const destinations = state.folders.filter(f => f.canWrite && !descendant(f) && (owner ? f.role === 'owner' : f.shareRootId === sourceRoot))
  const inherited = folder?.shareRootId != null && folder.shareRootId !== folder.id
  const canEdit = folder ? folder.canEdit : document?.canWrite !== false
  const errorView = error && <Notice>{error}</Notice>
  if (action.kind === 'folder' || action.kind === 'document') return <Dialog title={title} description={folder?.path} onClose={onClose}>
    <p className="mb-4 text-sm leading-6 text-muted">{folder ? `${folder.role === 'owner' ? 'Owner' : folder.role === 'write' ? 'Write access' : 'Read access'} · ${folder.documentCount} ${folder.documentCount === 1 ? 'document' : 'documents'} · ${folder.folderCount} ${folder.folderCount === 1 ? 'subfolder' : 'subfolders'}` : `${document?.chunks} passages${document?.addedBy ? ` · Added by ${document.addedBy}` : ''}`}{inherited && <><br />Access inherited from {folder?.shareRootName}. All descendants have the same access.</>}</p>
    <div className="flex flex-col gap-1">
      {folder?.canWrite && <><Button variant="ghost" className="justify-start" onClick={() => onAdd(folder.id)}><PlusIcon size={18} aria-hidden />Add a document</Button><Button variant="ghost" className="justify-start" onClick={() => onAction({ kind: 'create', parentId: folder.id })}><FolderPlusIcon size={18} aria-hidden />Create subfolder</Button></>}
      {canEdit && <><Button variant="ghost" className="justify-start" onClick={() => change('rename')}><PencilSimpleIcon size={18} aria-hidden />Rename</Button><Button variant="ghost" className="justify-start" onClick={() => change('move')}><ArrowBendUpRightIcon size={18} aria-hidden />Move to…</Button></>}
      {folder?.canManage && <Button variant="ghost" className="justify-start" onClick={() => inherited ? onAction({ kind: 'share', folder: state.folders.find(f => f.id === folder.shareRootId)! }) : change('share')}><UsersIcon size={18} aria-hidden />{inherited ? 'Manage inherited access' : 'Share folder'}</Button>}
      {folder && <Button variant="ghost" className="justify-start" onClick={() => {
        const url = new URL(window.location.href); url.search = ''; url.searchParams.set('folder', String(folder.id))
        navigator.clipboard.writeText(url.toString()).then(() => setCopied(true)).catch(() => setError('Unable to copy the link. Please try again.'))
      }}><LinkIcon size={18} aria-hidden />{copied ? 'Link copied · Existing access only' : 'Copy folder link'}</Button>}
      {folder && !folder.canManage && folder.shareRootId === folder.id && <Button variant="ghost" className="justify-start" onClick={() => change('leave')}><SignOutIcon size={18} aria-hidden />Leave shared folder</Button>}
      {canEdit && <Button variant="danger" className="mt-3 justify-start" onClick={() => change('delete')}><TrashIcon size={18} aria-hidden />Delete {folder ? 'folder' : 'document'}</Button>}
    </div>{errorView}
  </Dialog>
  if (action.kind === 'create' || action.kind === 'rename') return <Dialog title={action.kind === 'create' ? 'Create folder' : `Rename ${folder ? 'folder' : 'document'}`} onClose={close} dismissible={!busy}>
    <form className="space-y-5" onSubmit={e => { e.preventDefault(); void run(() => action.kind === 'create' ? api.createFolder(name.trim(), action.parentId ?? null) : api.renameItem(kind, id, name.trim())) }}>
      <p className="break-words text-xs text-muted">Location: {state.folders.find(f => f.id === (action.kind === 'create' ? action.parentId : folder?.parentId ?? document?.folderId))?.path ?? 'My library · Private'}</p>
      <Field label="Name" autoFocus required maxLength={256} value={name} onChange={e => setName(e.target.value)} disabled={busy} />{errorView}
      <div className="flex justify-end gap-2"><Button variant="secondary" onClick={close} disabled={busy}>Cancel</Button><Button type="submit" busy={busy} disabled={!name.trim()}>{action.kind === 'create' ? 'Create folder' : 'Save name'}</Button></div>
    </form>
  </Dialog>
  if (action.kind === 'move') return <Dialog title={`Move ${title}`} onClose={close} dismissible={!busy}>
    <form className="space-y-5" onSubmit={e => { e.preventDefault(); void run(() => api.moveItem(kind, id, destination ? Number(destination) : null, confirmed)) }}>
      <div className="space-y-2"><label htmlFor="move-destination" className="block text-sm font-medium">Destination folder</label><FolderSelect autoFocus id="move-destination" label="Destination folder" value={destination === '' ? null : Number(destination)} folders={destinations} includeRoot={owner} rootLabel={folder ? 'My folders' : 'Unfiled documents'} disabled={busy} onValueChange={next => { setDestination(next === null ? '' : String(next)); setConfirmed(false) }} /></div>
      <p className="break-words text-sm leading-6 text-muted">Destination: {target?.path ?? 'My library'}<br />{targetRoot ? `Access will follow ${state.folders.find(f => f.id === targetRoot)?.name ?? 'the shared folder'}.` : folder && state.folders.some(f => descendant(f) && f.shareRootId === f.id) ? 'This folder stays private. Existing shares inside it are preserved.' : 'Only you will have access.'}</p>
      {accessChanges && <label className="flex items-start gap-3 rounded-control bg-primary/5 p-3 text-sm leading-6"><input type="checkbox" className="mt-1.5 size-4 shrink-0 accent-accent" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} disabled={busy} />I understand that existing members may lose access and destination members will gain access to this content.</label>}
      {errorView}<div className="flex justify-end gap-2"><Button variant="secondary" onClick={close} disabled={busy}>Cancel</Button><Button type="submit" busy={busy} disabled={(accessChanges && !confirmed) || (!owner && !target)}>Move here</Button></div>
    </form>
  </Dialog>
  if (action.kind === 'share' && folder) return <Dialog title={`Share ${title}`} description="Access applies to every document and subfolder, including future additions." onClose={close} dismissible={!busy}>
    <form className="space-y-4" onSubmit={e => { e.preventDefault(); void run(() => api.addMember(folder.id, email.trim(), role), true) }}>
      <Field label="Registered user's email" type="email" autoFocus required value={email} onChange={e => setEmail(e.target.value)} disabled={busy} />
      <div className="flex flex-wrap items-end gap-3"><div className="min-w-0 flex-1 space-y-2"><label htmlFor="share-role" className="block text-sm font-medium">Access</label><Select id="share-role" label="Access" value={role} onValueChange={setRole} disabled={busy} options={[{ value: 'read', label: 'Read', description: 'Consult and ask questions' }, { value: 'write', label: 'Write', description: 'Also manage folder content' }]} /></div><Button type="submit" busy={busy} disabled={!email.trim() || membersLoading}>Give access</Button></div>
      <p className="text-xs leading-5 text-muted">Read: consult and ask questions. Write: also add, rename, move and delete content. Only you manage sharing.</p>
    </form>
    <section className="mt-6 border-t border-line pt-4" aria-label="Folder members"><h3 className="text-sm font-semibold">People with access</h3><p className="py-3 text-sm text-muted">You · Owner</p>{membersLoading ? <p role="status" className="text-sm text-muted">Loading members…</p> : members.map(member => <div key={member.userId} className="flex flex-wrap items-center gap-2 border-t border-line py-3"><span className="min-w-0 flex-1 break-words text-sm">{member.email}</span><Select className="w-24" label={`Access for ${member.email}`} value={member.role} disabled={busy} onValueChange={next => void run(() => api.changeMember(folder.id, member.userId, next), true)} options={[{ value: 'read', label: 'Read' }, { value: 'write', label: 'Write' }]} /><Button variant="ghost" className="px-2 text-xs" disabled={busy} onClick={() => void run(() => api.removeMember(folder.id, member.userId), true)}>Remove</Button></div>)}</section>
    {members.length > 0 && <Button variant="danger" className="mt-4" disabled={busy} onClick={() => change('stop')}>Stop sharing</Button>}{error && <div className="mt-4">{errorView}</div>}
  </Dialog>
  const leaving = action.kind === 'leave'
  const stopping = action.kind === 'stop'
  return <Dialog title={leaving ? `Leave ${title}?` : stopping ? `Stop sharing ${title}?` : `Delete ${title}?`} onClose={close} dismissible={!busy}>
    <p className="mb-5 text-sm leading-6 text-muted">{leaving ? 'You will lose access to this folder and its contents. Other members keep their access.' : stopping ? 'All members will lose access to this folder and its descendants. Your documents will remain in your library.' : folder ? `Permanently delete this folder, ${folder.folderCount} subfolders and ${folder.documentCount} documents?${folder.shareRootId || state.folders.some(f => descendant(f) && f.shareRootId === f.id) ? ' Shared members will lose access to the deleted content.' : ''} This cannot be undone.` : 'Permanently delete this document and its passages? This cannot be undone.'}</p>
    {errorView}<div className="mt-5 flex justify-end gap-2"><Button variant="secondary" onClick={close} disabled={busy}>Cancel</Button><Button variant="danger" busy={busy} onClick={() => void run(() => leaving ? api.leaveFolder(id) : stopping ? api.stopSharing(id) : folder ? api.deleteFolder(id) : api.deleteDoc(id))}>{leaving ? 'Leave folder' : stopping ? 'Stop sharing' : 'Delete permanently'}</Button></div>
  </Dialog>
}
