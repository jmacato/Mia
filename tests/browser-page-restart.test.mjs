import assert from 'node:assert/strict'
import test from 'node:test'

import { reloadForEmulatorRestart } from
    '../src/Mia.Browser/wwwroot/browser-page.js'

test('emulator restart replaces the complete browser runtime', () => {
    const originalLocation = globalThis.location
    let replacement = null
    globalThis.location = {
        href: 'https://localhost:3477/?noauto',
        replace(value) {
            replacement = value
        },
    }

    try {
        reloadForEmulatorRestart()
        assert.equal(
            replacement,
            'https://localhost:3477/')
    } finally {
        if (originalLocation === undefined) {
            delete globalThis.location
        } else {
            globalThis.location = originalLocation
        }
    }
})
