# INDEX design system

AgentRAG uses a quiet ivory and graphite palette, an editorial display face, and simple controls. The source of truth is `src/index.css`, using Tailwind CSS 4's CSS-first `@theme` configuration and [Vite integration](https://tailwindcss.com/docs/installation/using-vite).

## Foundations

Use semantic utilities instead of literal colors: `bg-canvas` for the page, `bg-surface` for fields, `bg-subtle` for grouping, `text-ink` for primary content, `text-muted` for supporting text, and `border-line` for separators. `accent` represents interactive focus; `danger` and `danger-surface` represent errors and destructive actions.

DM Sans is the interface font (`font-sans`); Newsreader is reserved for introductory headlines (`font-display`). Fonts are bundled locally with the app. Body text is 15–16px; supporting text is 12–14px. Prefer whitespace and dividers over cards and shadows. Controls use `rounded-control`, dialogs use `rounded-panel`.

## Components

Import shared primitives from `src/components/ui.tsx`: `Button` (primary, secondary, ghost, danger), `IconButton` (requires a label), `Field` (connected label and optional hint), `Notice` (error alert), and `Dialog` (native focus management). Button classes are merged with `tailwind-merge`, so local utility overrides replace defaults reliably. Use `Brand` for the logo and wordmark. Icons come from [Phosphor](https://github.com/phosphor-icons/react), in regular weight.

```tsx
<Field label="Document title" value={title} onChange={onChange} />
<Button busy={saving} type="submit">Add document</Button>
```

Feature code lives under `src/features/{auth,chat,documents,workspace}`. API operations stay in `src/api.ts`; avoid duplicating fetch calls in components.

## Responsive and accessible by default

Write the smallest layout first, then add `sm:` and `lg:` enhancements. On mobile, documents open in a modal; at 1024px they become a persistent sidebar. Use `dvh` for the workspace and safe-area padding for the composer. The conversation scrolls independently. Keep controls at least 44px high and input text at least 16px. Touch users submit with the send button and can insert newlines; desktop users can also press Enter (Shift+Enter inserts a newline).

Preserve labels, focus rings, reduced-motion support, status messages and keyboard access. All interface copy is English. Do not fabricate citations: the current chat API returns Markdown text containing document-title references, not structured source excerpts.

## Branding

`public/brand/mark.png` is the transparent INDEX logo mark. `public/favicon-32.png`, `favicon-64.png`, and `apple-touch-icon.png` derive from that mark. The browser favicons use an opaque white background for visibility in dark browser tabs. The mark was generated with ImageGen from the selected design; there are no external asset requests at runtime. Replace these files and the `Brand` wordmark to rebrand a fork.
