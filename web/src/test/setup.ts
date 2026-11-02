import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

afterEach(() => { cleanup(); vi.unstubAllGlobals(); localStorage.clear() })
Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { value: vi.fn(), configurable: true })
Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { value: function (this: HTMLDialogElement) { this.setAttribute('open', '') }, configurable: true })
Object.defineProperty(HTMLDialogElement.prototype, 'close', { value: function (this: HTMLDialogElement) { this.removeAttribute('open') }, configurable: true })
Object.defineProperty(window, 'matchMedia', { value: vi.fn().mockImplementation(() => ({ matches: true })), configurable: true })
