import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from '../../api'

export function useLibrary() {
  const [library, setLibrary] = useState<api.Library>({ folders: [], documents: [], revision: '' })
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const request = useRef<AbortController | null>(null)
  const revision = useRef('')
  const [resetCount, setResetCount] = useState(0)
  const invalidate = useCallback(() => {
    request.current?.abort()
    setLibrary({ folders: [], documents: [], revision: '' })
    setResetCount(count => count + 1)
  }, [])
  const refresh = useCallback(async () => {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    try {
      const next = await api.listLibrary(controller.signal)
      if (controller.signal.aborted) return
      if (revision.current && revision.current !== next.revision) setResetCount(count => count + 1)
      revision.current = next.revision
      setLibrary(next)
      setError('')
    } catch (e) {
      if (controller.signal.aborted) return
      invalidate()
      setError(api.errorMessage(e))
      setLoading(false)
    } finally { if (!controller.signal.aborted) setLoading(false) }
  }, [invalidate])
  useEffect(() => {
    const initial = window.setTimeout(() => { void refresh() }, 0)
    const changed = () => { invalidate(); void refresh() }
    const focused = () => { if (document.visibilityState === 'visible') void refresh() }
    window.addEventListener('agentrag:access-changed', changed)
    window.addEventListener('focus', focused)
    document.addEventListener('visibilitychange', focused)
    const timer = window.setInterval(focused, 30000)
    return () => {
      window.clearTimeout(initial); window.clearInterval(timer); request.current?.abort()
      window.removeEventListener('agentrag:access-changed', changed)
      window.removeEventListener('focus', focused)
      document.removeEventListener('visibilitychange', focused)
    }
  }, [invalidate, refresh])
  return {
    ...library, loading, error, refresh, resetCount,
    add: async (title: string, content: string | File, onProgress?: (progress: api.IngestionProgress) => void, signal?: AbortSignal, folderId?: number | null, index = true) => {
      await api.addDoc(title, content, onProgress, signal, folderId, index)
      await refresh()
    },
    remove: async (id: number) => { await api.deleteDoc(id); await refresh() },
  }
}

export type LibraryState = ReturnType<typeof useLibrary>
