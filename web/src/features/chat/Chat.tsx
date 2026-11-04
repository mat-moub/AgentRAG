import { useEffect, useRef, useState } from 'react'
import type { SubmitEvent } from 'react'
import { ArrowRightIcon, ArrowUpIcon, CircleNotchIcon, FileTextIcon, PlusIcon, UserIcon } from '@phosphor-icons/react'
import Markdown from 'react-markdown'
import { Brand } from '../../components/Brand'
import { Button, Notice } from '../../components/ui'
import * as api from '../../api'

interface Message { id: number; role: 'user' | 'assistant'; text: string; streaming?: boolean; incomplete?: boolean }

export function Chat({ documentCount, documentsReady, onAdd }: { documentCount: number; documentsReady: boolean; onAdd: () => void }) {
  const [messages, setMessages] = useState<Message[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [retry, setRetry] = useState('')
  const bottom = useRef<HTMLDivElement>(null)
  const textarea = useRef<HTMLTextAreaElement>(null)
  const sending = useRef(false)
  const nextId = useRef(0)
  const request = useRef<AbortController | null>(null)
  const ready = documentsReady && documentCount > 0
  const writing = messages.some(message => message.streaming && message.text.length > 0)

  useEffect(() => () => request.current?.abort(), [])
  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'instant', block: 'end' }) }, [messages, busy, error])
  useEffect(() => {
    if (!busy && messages.length > 0 && window.matchMedia('(pointer: fine)').matches) textarea.current?.focus()
  }, [busy, messages.length])

  async function send(text: string, retrying = false) {
    const question = text.trim()
    if (!question || sending.current || !ready) return
    // Only completed exchanges become context; failed replies and retries stay out.
    const history: api.ChatMessage[] = []
    for (let index = 1; index < messages.length; index++) {
      const previous = messages[index - 1]
      const reply = messages[index]
      if (previous.role === 'user' && reply.role === 'assistant' && reply.text && !reply.streaming && !reply.incomplete) {
        history.push({ role: 'user', content: previous.text }, { role: 'assistant', content: reply.text })
      }
    }
    sending.current = true
    setBusy(true); setError(''); setRetry(''); setInput('')
    const userId = nextId.current++
    const replyId = nextId.current++
    const controller = new AbortController()
    request.current = controller
    setMessages(current => {
      const previous = retrying && current.at(-1)?.incomplete ? current.slice(0, -1) : current
      return [...previous, ...(!retrying ? [{ id: userId, role: 'user' as const, text: question }] : []),
        { id: replyId, role: 'assistant', text: '', streaming: true }]
    })
    try {
      await api.chat(question, text => {
        if (!controller.signal.aborted)
          setMessages(current => current.map(message => message.id === replyId ? { ...message, text: message.text + text } : message))
      }, controller.signal, history)
      if (!controller.signal.aborted)
        setMessages(current => current.map(message => message.id === replyId ? { ...message, streaming: false } : message))
    } catch (e) {
      if (!controller.signal.aborted) {
        setMessages(current => current.flatMap(message => message.id !== replyId ? [message]
          : message.text ? [{ ...message, streaming: false, incomplete: true }] : []))
        setError(api.errorMessage(e)); setRetry(question)
      }
    } finally { sending.current = false; if (!controller.signal.aborted) setBusy(false) }
  }

  function submit(e: SubmitEvent<HTMLFormElement>) { e.preventDefault(); void send(input) }

  return <div className="flex min-h-0 flex-1 flex-col">
    <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-5 sm:px-10" role="region" aria-label="Conversation" tabIndex={0}>
      <div className="mx-auto flex min-h-full max-w-2xl flex-col">
        {messages.length === 0 ? <div className="flex flex-1 flex-col justify-center py-10 sm:py-16">
          <p className="eyebrow mb-5">Your research space</p>
          <h1 className="font-display text-[clamp(2.4rem,7vw,3.5rem)] leading-[1.08] tracking-[-0.025em]">Less searching.<br />More understanding.</h1>
          <p className="mt-5 max-w-md text-[15px] leading-7 text-muted">{!documentsReady ? 'Your documents will appear here once they are loaded.' : documentCount ? 'Ask a question. Find clarity in your documents, with sources you can trace.' : 'Start with a document. Then ask questions and find answers in what you already know.'}</p>
          {!documentCount ? <div className="mt-7"><Button onClick={onAdd} disabled={!documentsReady}><PlusIcon size={18} aria-hidden />Add your first document</Button></div>
            : <div className="mt-8 max-w-md divide-y divide-line border-y border-line">{['Summarize the key ideas in my documents.', 'What are the most important takeaways?'].map(question => <button key={question} className="flex min-h-14 w-full items-center justify-between gap-4 py-3 text-left text-sm transition-colors hover:text-accent disabled:opacity-50" disabled={!ready} onClick={() => void send(question)}>{question}<ArrowRightIcon size={18} className="shrink-0" aria-hidden /></button>)}</div>}
        </div> : <div className="space-y-9 py-7 sm:space-y-10 sm:py-10" role="log" aria-label="Messages" aria-live="polite" aria-relevant="additions text">
          {messages.filter(message => message.role === 'user' || message.text).map(message => <article key={message.id} aria-busy={message.streaming || undefined} aria-label={message.role === 'user' ? 'Your question' : 'AgentRAG answer'} className="animate-enter">
            {message.role === 'user' ? <div className="ml-auto flex max-w-[95%] items-start gap-3 rounded-control bg-subtle px-4 py-3 sm:max-w-[90%]"><UserIcon size={20} className="mt-0.5 shrink-0 text-muted" aria-hidden /><p className="min-w-0 text-[15px] leading-7 whitespace-pre-wrap [overflow-wrap:anywhere]">{message.text}</p></div>
              : <div><div className="mb-4 flex items-center gap-3"><Brand compact /><span className="text-sm font-semibold">AgentRAG</span></div><div className="answer"><Markdown skipHtml components={{ a: ({ children, href }) => <a href={href} target="_blank" rel="noopener noreferrer">{children}</a>, img: ({ alt }) => <span>{alt}</span> }}>{message.text}</Markdown></div>{message.incomplete && <p className="mt-3 text-xs text-muted">Incomplete response</p>}</div>}
          </article>)}
        </div>}
        {busy && <p role="status" className="mb-6 flex items-center gap-2 text-sm text-muted"><CircleNotchIcon size={18} className="motion-safe:animate-spin" aria-hidden />{writing ? 'Writing an answer…' : 'Searching your documents…'}</p>}
        {error && <div className="mb-5"><Notice>{error}<button type="button" className="ml-2 min-h-11 underline underline-offset-4" onClick={() => void send(retry, true)} disabled={!ready}>Try again</button></Notice></div>}
        <div ref={bottom} />
      </div>
    </div>
    <div className="shrink-0 bg-canvas px-4 pt-3 pb-[max(1rem,env(safe-area-inset-bottom))] sm:px-10 sm:pb-6">
      <form onSubmit={submit} className="mx-auto flex max-w-2xl items-end gap-2 rounded-control border border-line bg-surface p-2 focus-within:border-accent focus-within:ring-1 focus-within:ring-accent">
        <label htmlFor="question" className="sr-only">Ask a question about your documents</label>
        <textarea ref={textarea} id="question" rows={2} value={input} onChange={e => setInput(e.target.value)} onKeyDown={e => {
          if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && window.matchMedia('(pointer: fine)').matches) { e.preventDefault(); void send(input) }
        }} placeholder={documentCount ? 'Ask about your documents…' : 'Add a document to start researching…'} disabled={busy || !ready} className="max-h-40 min-h-12 min-w-0 flex-1 resize-none bg-transparent px-2 py-2 text-base leading-6 placeholder:text-muted focus:outline-none disabled:opacity-60" />
        <Button type="submit" aria-label="Send question" title="Send question" className="size-11 p-0" disabled={busy || !ready || !input.trim()}><ArrowUpIcon size={21} aria-hidden /></Button>
      </form>
      <p className="mx-auto mt-3 flex max-w-2xl items-start justify-center gap-1.5 text-center text-[11px] leading-4 text-muted"><FileTextIcon size={14} className="shrink-0" aria-hidden />Answers come from your documents. Always check the sources.</p>
    </div>
  </div>
}
