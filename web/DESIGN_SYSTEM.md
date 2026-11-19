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

`public/brand/mark.png` is the original transparent INDEX logo mark. `public/brand/icon-round.png` is its circular application icon, also exported as `favicon-32.png`, `favicon-64.png`, and `apple-touch-icon.png`. The dark symbol sits on a white disk with transparent corners, so the icon remains readable on light and dark backgrounds without a square backing. The icon was edited with ImageGen and exported with a clean circular alpha mask. There are no external asset requests at runtime. Replace these files and the `Brand` wordmark to rebrand a fork.

## Warm palette

Primary orange: `#b84b16`; secondary terracotta: `#895344`; tertiary ochre: `#a06a18`. These analogous warm hues share the ivory canvas and graphite text. Use `primary` for actions, exact matches and selected passages; `secondary` for semantic similarity; `tertiary` for the unsearched map. Accent now follows primary. Keep textual labels alongside chart colors.

## Chart colors

Categorical data (documents on the semantic map) uses eight softer hues declared in `src/index.css` as `--color-chart-*`: `blue` `#32649b`, `teal` `#388779`, `purple` `#854899`, `terracotta` `#c05d3c`, `ochre` `#c28a2c`, `deep-teal` `#356d78`, `mauve` `#8b5572`, `olive` `#657532`. Use the CSS variables (`var(--color-chart-blue)`, or `bg-chart-blue`), never the literal hex values, so one edit rethemes every chart. The wiki graph uses colour for the family and shape for the kind, with only two hue families: documentation in warm terracotta and ochre (feature = filled circle, business rule = ring, concept = smaller ochre circle) and code in cool teal and deep teal (module = larger rounded square, component = square, data entity = hollow square, API = hexagon). Overview is an ink circle with an outer ring; sources are small hollow diamonds in `muted`. Nodes are opaque with a 1.5px surface outline so overlaps stay distinct. Nodes grow with their number of links; features and modules, the hubs, grow faster and go from half to full opacity as they gain links. Links are neutral: `implements` is ink at about 50%, 1.5px with an arrow, between `related` and full ink, and comes back to about 90% around the hovered page; `part-of` and `depends-on` (dashed) are deep teal at about 45%; `related`, `mentions` and `source` are faint muted lines, and `related` and `mentions` are hidden by default. Colors are never the only cue: shapes (round for documentation, angular for code), dash patterns and the legend carry the same information.
