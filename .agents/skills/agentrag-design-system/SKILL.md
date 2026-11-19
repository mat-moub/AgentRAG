---
name: agentrag-design-system
description: The INDEX design system of AgentRAG — Tailwind CSS 4 semantic tokens in web/src/index.css (canvas, surface, subtle, ink, muted, line, primary/secondary/tertiary warm palette, danger, chart colors), DM Sans and Newsreader typography, radii, shared primitives (Button, IconButton, Field, Notice, Dialog, Select, FolderSelect, Brand), Phosphor icons, accessibility and responsive rules, chart and wiki-graph color/shape encoding, branding assets. Use this skill whenever styling, theming, adding or modifying a UI component, choosing a color or icon, building a chart or graph, rebranding a fork, or reviewing UI for accessibility — even if the user only says "make it look nicer" or "change the color".
---

# INDEX design system

Source of truth: `web/src/index.css` (`@theme` tokens) and `web/DESIGN_SYSTEM.md`; component API notes in
`web/src/components/README.md`. Read those files before larger visual changes.

## Tokens (use utilities, never literal hex)

| Role | Utility | Value |
|------|---------|-------|
| Page background | `bg-canvas` | `#f8f7f4` ivory |
| Fields / raised | `bg-surface` | `#fdfcfb` |
| Grouping | `bg-subtle` | `#efeeeb` |
| Primary text | `text-ink` | `#252724` graphite |
| Supporting text | `text-muted` | `#666862` |
| Separators | `border-line` | `#deded8` |
| Actions, exact matches, selection | `primary` / `accent` | `#b84b16` orange |
| Semantic similarity | `secondary` | `#895344` terracotta |
| Unsearched map | `tertiary` | `#a06a18` ochre |
| Errors / destructive | `danger`, `danger-surface` | `#a3332b`, `#fcf0ed` |
| Radii | `rounded-control`, `rounded-panel` | 0.375rem, 0.75rem |
| Entrance | `animate-enter` | 180 ms fade/slide |

Chart palette (`@theme static`): `--color-chart-{blue,teal,purple,terracotta,ochre,deep-teal,mauve,olive}`;
reference them as `var(--color-chart-x)` or `bg-chart-x` so one edit rethemes every chart.

Typography: `font-sans` DM Sans for the interface; `font-display` Newsreader only for introductory
headlines. Body 15–16 px, supporting 12–14 px. Fonts are bundled (no remote font provider).

Style: quiet and editorial — prefer whitespace and dividers over cards and shadows.

## Components (`web/src/components`)

- `Button` — variants `primary`, `secondary`, `ghost`, `danger`; `busy` state; classes merged with
  `tailwind-merge`, so local overrides win.
- `IconButton` — requires `label` (accessible name).
- `Field` — label + input + optional hint, connected ids.
- `Notice` — error alert. `Dialog` — native `<dialog>` with focus management, `dismissible`, `wide`.
- `Select` — controlled single select with keyboard navigation, typeahead, optional search, popover top
  layer; use it instead of native `<select>`. `FolderSelect` wraps it for folder destinations and scope.
- `Brand` (logo + wordmark), `ProgressRing`, `SiteFooter`. Icons: Phosphor, regular weight.

Create a new shared primitive only when a pattern is used in two or more features; otherwise keep it in the
feature folder. Document new primitives in `components/README.md`.

## Accessibility and responsiveness (non-negotiable)

- Mobile first, then `sm:` / `lg:`; `dvh` for the workspace, safe-area padding for the composer.
- Controls ≥ 44 px high, input text ≥ 16 px (avoids iOS zoom).
- Visible focus (`:focus-visible` uses the accent outline), labels on every control, status messages with
  `role="status"`/`alert`, full keyboard access, `prefers-reduced-motion` respected.
- Color is never the only cue: pair with text, shape or dash pattern.

## Data visualization encoding

- Semantic map: documents colored with the categorical chart palette, always with a legend/label.
- Wiki graph (`features/wiki/wikiGraphStyle.ts`): color = family, shape = kind.
  Documentation in warm terracotta/ochre (feature = filled circle, rule = ring, concept = small ochre circle);
  code in teal/deep teal (module = large rounded square, component = square, data-entity = hollow square,
  api = hexagon); overview = ink circle with outer ring; sources = small hollow muted diamonds.
  Hubs (feature, module) grow faster with their links and go from 50 % to full opacity. Links: `implements` ink ~50 %, 1.5 px with arrow, ~90 % around the hovered page; `part-of`/`depends-on` (dashed) deep teal ~45 %; `related`,
  `mentions`, `source` faint muted, `related` and `mentions` hidden by default.

## Branding a fork

Replace `public/brand/mark.png`, `public/brand/icon-round.png`, favicons and the `Brand` wordmark; edit tokens
in `index.css`. Keep the API's `wwwroot/brand` and Razor `public-pages.css` consistent with the SPA.
