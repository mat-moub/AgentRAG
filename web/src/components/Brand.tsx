export function Brand({ compact = false }: { compact?: boolean }) {
  return <span className="inline-flex shrink-0 items-center gap-2.5" aria-label="AgentRAG">
    <img src={`${import.meta.env.BASE_URL}brand/icon-round.png`} alt="" width="36" height="36" className="size-9 rounded-full object-contain" />
    {!compact && <span className="text-[23px] font-semibold tracking-[-0.055em]">AgentRAG</span>}
  </span>
}
