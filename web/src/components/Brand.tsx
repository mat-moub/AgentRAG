export function Brand({ compact = false }: { compact?: boolean }) {
  return <span className="inline-flex shrink-0 items-center gap-2.5" aria-label="AgentRAG">
    <img src={`${import.meta.env.BASE_URL}brand/mark.png`} alt="" width="32" height="36" className="h-9 w-8 object-contain" />
    {!compact && <span className="text-[23px] font-semibold tracking-[-0.055em]">AgentRAG</span>}
  </span>
}
