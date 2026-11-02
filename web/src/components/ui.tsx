import { useEffect, useId, useRef } from 'react'
import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode } from 'react'
import { CircleNotchIcon, WarningCircleIcon, XIcon } from '@phosphor-icons/react'
import { twMerge } from 'tailwind-merge'

const variants = {
  primary: 'border-transparent bg-ink text-surface hover:bg-accent',
  secondary: 'border-line bg-transparent text-ink hover:bg-subtle',
  ghost: 'border-transparent bg-transparent text-muted hover:bg-subtle hover:text-ink',
  danger: 'border-transparent bg-danger-surface text-danger hover:bg-danger hover:text-white',
}

export function Button({ variant = 'primary', busy = false, className = '', children, disabled, type = 'button', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: keyof typeof variants; busy?: boolean }) {
  return <button type={type} disabled={disabled || busy} aria-busy={busy || undefined} className={twMerge('inline-flex min-h-11 shrink-0 items-center justify-center gap-2 rounded-control border px-4 py-2.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 [&_svg]:shrink-0', variants[variant], className)} {...props}>
    {busy && <CircleNotchIcon aria-hidden size={18} className="motion-safe:animate-spin" />}{children}
  </button>
}

export function IconButton({ label, children, className = '', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { label: string }) {
  return <Button variant="ghost" aria-label={label} title={label} className={`size-11 p-0 ${className}`} {...props}>{children}</Button>
}

export function Field({ label, hint, id, ...props }: InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string }) {
  const generatedId = useId()
  const fieldId = id ?? generatedId
  return <div className="space-y-2">
    <label htmlFor={fieldId} className="block text-sm font-medium">{label}</label>
    <input id={fieldId} className="field" aria-describedby={hint ? `${fieldId}-hint` : undefined} {...props} />
    {hint && <p id={`${fieldId}-hint`} className="text-xs leading-5 text-muted">{hint}</p>}
  </div>
}

export function Notice({ children }: { children: ReactNode }) {
  return <div role="alert" className="flex items-start gap-2.5 rounded-control bg-danger-surface p-3 text-sm leading-6 text-danger">
    <WarningCircleIcon size={20} className="mt-0.5 shrink-0" aria-hidden /><div className="min-w-0 break-words">{children}</div>
  </div>
}

/** Native modal supplies focus trapping, Escape handling and focus restoration. */
export function Dialog({ title, onClose, children, description, dismissible = true }: { title: string; description?: string; onClose: () => void; children: ReactNode; dismissible?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const descriptionId = useId()
  useEffect(() => {
    const dialog = ref.current!
    dialog.showModal()
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => { dialog.close(); document.body.style.overflow = previousOverflow }
  }, [])
  return <dialog ref={ref} aria-labelledby={titleId} aria-describedby={description ? descriptionId : undefined} onCancel={e => { e.preventDefault(); if (dismissible) onClose() }} onClick={e => { if (e.target === e.currentTarget && dismissible) onClose() }} className="fixed inset-0 m-auto max-h-[90dvh] w-[calc(100%-2rem)] max-w-lg overflow-y-auto rounded-panel border border-line bg-canvas p-0 text-ink">
    <div className="p-5 sm:p-7">
      <header className="mb-5 flex items-start justify-between gap-4">
        <div><h2 id={titleId} className="text-lg font-semibold tracking-tight">{title}</h2>{description && <p id={descriptionId} className="mt-1 text-sm leading-6 text-muted">{description}</p>}</div>
        <IconButton label="Close dialog" disabled={!dismissible} onClick={onClose} className="-mt-2 -mr-2"><XIcon size={20} /></IconButton>
      </header>
      {children}
    </div>
  </dialog>
}
