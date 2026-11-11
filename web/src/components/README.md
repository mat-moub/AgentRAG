# AgentRAG controls

Use the existing surface, line, ink, muted and accent tokens from `index.css`. Shared controls are exported by `ui.tsx`; Phosphor supplies icons.

## Select

`Select` is the controlled single-selection dropdown for AgentRAG. Use it instead of native `<select>` controls to keep the trigger, option list, keyboard highlighting and selected checkmark consistent.

```tsx
import { Select } from './ui'

<Select
  label="Access"
  value={role}
  onValueChange={setRole}
  options={[
    { value: 'read', label: 'Read', description: 'Consult and ask questions' },
    { value: 'write', label: 'Write', description: 'Also manage folder content' },
  ]}
/>
```

- Required props: `label`, `value`, `options`, `onValueChange`. Values are strings; an empty string is a valid value.
- Options support `description`, `icon`, `meta`, `depth`, `disabled`, and `keywords`. `selectedLabel` displays a longer context, such as a full folder path, on the closed trigger and names the option for assistive technology.
- Set `searchable` for longer lists. Search matches labels, descriptions and keywords without case or accent sensitivity. Customize `searchPlaceholder` and `emptyMessage` as needed.
- Use `id` with a visible `<label htmlFor>` and `aria-describedby` for guidance. `label` remains the required accessible name. `name` adds a hidden form field when native FormData submission is needed.
- Navigation: arrows, Home/End, Enter/Space, typeahead for short lists, Tab to leave, Escape to cancel without closing a parent dialog. Moving through options does not commit a value; choosing the current value closes without calling `onValueChange`.
- The native popover top layer keeps menus visible above modal dialogs and scroll containers. Positioning adapts above/below the trigger, to viewport edges, scrolling, resizing and the mobile keyboard. Browsers without the Popover API use the same positioned menu inline.
- Outside clicks dismiss without taking focus from the clicked control. Choosing or cancelling restores focus to the trigger. Disabled options cannot be selected.

## FolderSelect

Use `FolderSelect` for document destinations, moves and research scope. It wraps `Select` with folder names, full selected paths, hierarchy, private/shared metadata, access descriptions and folder search.

Its controlled `value` is a folder ID or `null` for the virtual root. Supply only folders allowed for the operation; the component never grants permissions. Configure `rootLabel`, `rootDescription` and `includeRoot` for the context. An unavailable selected folder remains visible as a disabled option, so it cannot silently become a different destination.

```tsx
import { FolderSelect } from './FolderSelect'

<FolderSelect
  label="Destination"
  folders={folders.filter(folder => folder.canWrite)}
  value={folderId}
  onValueChange={setFolderId}
/>
```
