import { publicPageUrl } from '../api'

const links = [
  { path: '/about', label: 'About' },
  { path: '/support', label: 'Support' },
  { path: '/privacy', label: 'Privacy policy' },
  { path: '/terms', label: 'Terms of service' },
] as const

export function SiteInformationLinks({ className }: { className: string }) {
  return <>{links.map(link => <a key={link.path} href={publicPageUrl(link.path)} target="_blank" rel="noopener noreferrer" className={className}>{link.label}<span className="sr-only"> (opens in a new tab)</span></a>)}</>
}

export function SiteFooter() {
  return <footer className="shrink-0 px-6 pb-6 text-center text-xs leading-5 text-muted sm:pb-8">
    <nav aria-label="Site information" className="flex flex-wrap items-center justify-center gap-x-4">
      <SiteInformationLinks className="inline-flex min-h-11 items-center underline decoration-transparent underline-offset-4 hover:text-ink hover:decoration-current focus-visible:decoration-current" />
    </nav>
    <p><a href="https://github.com/mat-moub/AgentRAG#agentrag" target="_blank" rel="noopener noreferrer" className="underline decoration-transparent underline-offset-4 hover:text-ink hover:decoration-current focus-visible:decoration-current">Open source</a> · Mathieu MOUBRI 2026</p>
  </footer>
}
