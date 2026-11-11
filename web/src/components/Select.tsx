import { useId, useLayoutEffect, useRef, useState } from 'react'
import type { KeyboardEvent, ReactNode } from 'react'
import { CaretDownIcon, CheckIcon, MagnifyingGlassIcon } from '@phosphor-icons/react'
import { twMerge } from 'tailwind-merge'

export interface SelectOption {
  value: string
  label: string
  selectedLabel?: string
  description?: string
  keywords?: string
  icon?: ReactNode
  meta?: string
  depth?: number
  disabled?: boolean
}

export interface SelectProps {
  id?: string
  name?: string
  label: string
  value: string
  options: SelectOption[]
  onValueChange: (value: string) => void
  disabled?: boolean
  searchable?: boolean
  searchPlaceholder?: string
  emptyMessage?: string
  placeholder?: string
  className?: string
  autoFocus?: boolean
  'aria-describedby'?: string
}

function normalize(value: string) {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLocaleLowerCase()
}

/** AgentRAG single select. The native popover top layer also works inside modal dialogs. */
export function Select({ id, name, label, value, options, onValueChange, disabled = false, searchable = false,
  searchPlaceholder = 'Search options…', emptyMessage = 'No matching options.', placeholder = 'Choose an option',
  className, autoFocus, 'aria-describedby': describedBy }: SelectProps) {
  const generatedId = useId()
  const triggerId = id ?? `${generatedId}-trigger`
  const listId = `${generatedId}-options`
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const popover = useRef<HTMLDivElement>(null)
  const search = useRef<HTMLInputElement>(null)
  const list = useRef<HTMLDivElement>(null)
  const typeahead = useRef({ text: '', at: 0 })
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [activeValue, setActiveValue] = useState<string | null>(null)
  if (disabled && open) setOpen(false)
  const expanded = open && !disabled
  const nativePopover = typeof HTMLElement !== 'undefined' && typeof HTMLElement.prototype.showPopover === 'function'
  const selected = options.find(option => option.value === value)
  const words = normalize(query).trim().split(/\s+/).filter(Boolean)
  const filtered = options.filter(option => words.every(word => normalize(`${option.label} ${option.selectedLabel ?? ''} ${option.description ?? ''} ${option.keywords ?? ''}`).includes(word)))
  const enabled = filtered.filter(option => !option.disabled)
  const active = enabled.find(option => option.value === activeValue) ?? enabled[0]
  const activeId = active ? `${listId}-${options.indexOf(active)}` : undefined

  function show(direction: 'first' | 'last' = 'first') {
    if (disabled) return
    setQuery('')
    const available = options.filter(option => !option.disabled)
    setActiveValue(available.find(option => option.value === value)?.value ?? (direction === 'last' ? available.at(-1)?.value : available[0]?.value) ?? null)
    setOpen(true)
  }

  function close(restoreFocus = false) {
    setOpen(false)
    if (restoreFocus) trigger.current?.focus()
  }

  function choose(option: SelectOption) {
    if (option.disabled) return
    close(true)
    if (option.value !== value) onValueChange(option.value)
  }

  useLayoutEffect(() => {
    const panel = popover.current
    if (!expanded || !panel) return
    panel.showPopover?.()
    function position() {
      if (!trigger.current || !panel) return
      const rect = trigger.current.getBoundingClientRect()
      const viewport = window.visualViewport
      const viewportTop = viewport?.offsetTop ?? 0
      const viewportLeft = viewport?.offsetLeft ?? 0
      const viewportHeight = viewport?.height ?? window.innerHeight
      const viewportWidth = viewport?.width ?? window.innerWidth
      const gap = 6, margin = 12
      const below = viewportTop + viewportHeight - rect.bottom - gap - margin
      const above = rect.top - viewportTop - gap - margin
      const upward = below < 240 && above > below
      const height = Math.max(0, Math.min(360, upward ? above : below))
      const width = Math.max(0, Math.min(Math.max(rect.width, searchable ? 300 : 180), viewportWidth - margin * 2))
      panel.style.width = `${width}px`
      panel.style.maxHeight = `${height}px`
      panel.style.left = `${Math.max(viewportLeft + margin, Math.min(rect.left, viewportLeft + viewportWidth - width - margin))}px`
      panel.style.top = `${upward ? rect.top - gap : rect.bottom + gap}px`
      panel.style.transform = upward ? 'translateY(-100%)' : ''
    }
    position()
    if (searchable) search.current?.focus()
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false)
    }
    document.addEventListener('pointerdown', outside, true)
    window.addEventListener('resize', position)
    window.addEventListener('scroll', position, true)
    window.visualViewport?.addEventListener('resize', position)
    window.visualViewport?.addEventListener('scroll', position)
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(position)
    if (trigger.current) observer?.observe(trigger.current)
    return () => {
      panel.hidePopover?.()
      document.removeEventListener('pointerdown', outside, true)
      window.removeEventListener('resize', position)
      window.removeEventListener('scroll', position, true)
      window.visualViewport?.removeEventListener('resize', position)
      window.visualViewport?.removeEventListener('scroll', position)
      observer?.disconnect()
    }
  }, [expanded, searchable])

  useLayoutEffect(() => {
    if (!expanded || !activeId) return
    const option = document.getElementById(activeId)
    const container = list.current
    if (!option || !container) return
    if (option.offsetTop < container.scrollTop) container.scrollTop = option.offsetTop
    else if (option.offsetTop + option.offsetHeight > container.scrollTop + container.clientHeight)
      container.scrollTop = option.offsetTop + option.offsetHeight - container.clientHeight
  }, [expanded, activeId])

  function keyDown(event: KeyboardEvent<HTMLElement>) {
    const typing = event.target === search.current
    if (event.key === 'Escape' && expanded) {
      event.preventDefault(); event.stopPropagation(); close(true)
    } else if (event.key === 'Tab') {
      close()
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!expanded) { show(event.key === 'ArrowUp' ? 'last' : 'first'); return }
      const index = enabled.findIndex(option => option.value === active?.value)
      const next = (index + (event.key === 'ArrowDown' ? 1 : -1) + enabled.length) % enabled.length
      setActiveValue(enabled[next]?.value ?? null)
    } else if ((event.key === 'Home' || event.key === 'End') && expanded && (!typing || !query)) {
      event.preventDefault()
      setActiveValue((event.key === 'Home' ? enabled[0] : enabled.at(-1))?.value ?? null)
    } else if (event.key === 'Enter' || (event.key === ' ' && !typing)) {
      event.preventDefault()
      if (!expanded) show()
      else if (active) choose(active)
    } else if (!typing && event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
      event.preventDefault()
      if (searchable) {
        if (!expanded) show()
        setQuery(event.key)
        setActiveValue(null)
        search.current?.focus()
      } else {
        const now = event.timeStamp
        const text = now - typeahead.current.at < 700 ? typeahead.current.text + event.key : event.key
        typeahead.current = { text, at: now }
        const available = options.filter(option => !option.disabled)
        const start = available.findIndex(option => option.value === (expanded ? active?.value : value)) + 1
        const rotated = [...available.slice(start), ...available.slice(0, start)]
        const prefix = normalize(text.split('').every(character => character === text[0]) ? text[0] : text)
        const match = rotated.find(option => normalize(option.label).startsWith(prefix))
        if (match) { setActiveValue(match.value); setOpen(true) }
      }
    }
  }

  return <div ref={root} className={twMerge('relative min-w-0 w-full', className)}>
    {name && <input type="hidden" name={name} value={value} disabled={disabled} />}
    <button ref={trigger} id={triggerId} type="button" role="combobox" aria-label={label} aria-haspopup="listbox"
      aria-expanded={expanded} aria-controls={expanded ? listId : undefined} aria-activedescendant={expanded && !searchable ? activeId : undefined}
      aria-describedby={describedBy} disabled={disabled} autoFocus={autoFocus} onKeyDown={keyDown}
      onClick={() => expanded ? close() : show()}
      className={`flex min-h-12 w-full items-center gap-2.5 rounded-control border bg-surface px-3.5 py-3 text-left text-sm text-ink transition-colors hover:border-muted/60 focus-visible:border-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent disabled:cursor-not-allowed disabled:opacity-60 ${expanded ? 'border-accent ring-1 ring-accent' : 'border-line'}`}>
      {selected?.icon && <span className={`shrink-0 ${selected.meta === 'Shared' ? 'text-accent' : 'text-muted'}`} aria-hidden>{selected.icon}</span>}
      <span className={`min-w-0 flex-1 truncate ${selected ? '' : 'text-muted'}`} title={selected?.selectedLabel ?? selected?.label}>{selected?.selectedLabel ?? selected?.label ?? placeholder}</span>
      <CaretDownIcon size={16} aria-hidden className={`shrink-0 text-muted transition-transform ${expanded ? 'rotate-180' : ''}`} />
    </button>
    <div ref={popover} popover={nativePopover ? 'auto' : undefined} hidden={!expanded} onToggle={event => { if (event.newState === 'closed') setOpen(false) }} onKeyDown={keyDown}
      className="select-popover fixed inset-auto m-0 flex flex-col overflow-hidden rounded-panel border border-line bg-surface p-0 text-ink shadow-[0_8px_28px_rgb(37_39_36/12%)]">
      {searchable && <div className="relative shrink-0 border-b border-line p-2"><MagnifyingGlassIcon size={18} aria-hidden className="pointer-events-none absolute top-1/2 left-5 -translate-y-1/2 text-muted" /><input ref={search} type="text" role="searchbox" aria-label={searchPlaceholder} aria-controls={listId} aria-activedescendant={activeId} autoComplete="off" spellCheck={false} value={query} onChange={event => { setQuery(event.target.value); setActiveValue(null) }} placeholder={searchPlaceholder} className="min-h-11 w-full rounded-control border-0 bg-transparent py-2 pr-3 pl-9 text-sm placeholder:text-muted focus:outline-none" /></div>}
      <div ref={list} id={listId} role="listbox" aria-label={`${label} options`} className="relative min-h-0 overflow-y-auto overscroll-contain p-1.5">
        {filtered.map(option => <div key={option.value} id={`${listId}-${options.indexOf(option)}`} role="option" aria-label={option.selectedLabel ?? option.label}
          aria-describedby={option.description ? `${listId}-${options.indexOf(option)}-description` : undefined}
          aria-selected={option.value === value} aria-disabled={option.disabled || undefined} onPointerMove={() => { if (!option.disabled) setActiveValue(option.value) }} onPointerDown={event => event.preventDefault()} onClick={() => choose(option)}
          className={`flex min-h-11 items-center gap-2.5 rounded-control py-2.5 pr-3 text-sm ${option.disabled ? 'cursor-not-allowed opacity-45' : 'cursor-pointer'} ${option.value === value ? 'bg-primary/8 text-ink' : option.value === active?.value ? 'bg-subtle' : ''}`}
          style={{ paddingLeft: `${12 + Math.min(option.depth ?? 0, 3) * 12}px` }}>
          {option.icon && <span aria-hidden className={`shrink-0 ${option.value === value ? 'text-accent' : 'text-muted'}`}>{option.icon}</span>}
          <span className="min-w-0 flex-1"><span className="block break-words font-medium">{option.label}</span>{option.description && <span id={`${listId}-${options.indexOf(option)}-description`} className="mt-0.5 block break-words text-xs leading-5 text-muted">{option.description}</span>}</span>
          {option.meta && <span className="shrink-0 text-[11px] text-muted">{option.meta}</span>}
          <span className="flex size-4 shrink-0 items-center justify-center">{option.value === value && <CheckIcon size={16} weight="bold" aria-hidden className="text-accent" />}</span>
        </div>)}
        {!filtered.length && <p role="status" className="px-3 py-5 text-center text-sm text-muted">{emptyMessage}</p>}
      </div>
    </div>
  </div>
}
