import { useCallback, useEffect, useState } from 'react'
import * as api from '../../api'

export function useDocuments() {
  const [documents, setDocuments] = useState<api.Doc[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const refresh = useCallback(async () => {
    setLoading(true); setError('')
    try { setDocuments(await api.listDocs()) }
    catch (e) { setError(api.errorMessage(e)) }
    finally { setLoading(false) }
  }, [])
  useEffect(() => {
    let active = true
    api.listDocs()
      .then(result => { if (active) setDocuments(result) })
      .catch(e => { if (active) setError(api.errorMessage(e)) })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [])
  return {
    documents, loading, error, refresh,
    add: async (title: string, content: string) => {
      const document = await api.addDoc(title, content)
      setDocuments(current => [document, ...current])
    },
    remove: async (id: number) => {
      await api.deleteDoc(id)
      setDocuments(current => current.filter(document => document.id !== id))
    },
  }
}

export type DocumentsState = ReturnType<typeof useDocuments>
