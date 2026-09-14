import assert from 'node:assert/strict'
import test from 'node:test'

import { loadText } from
    '../src/Mia.Browser/wwwroot/persistence.js'

const storage = new Map()
const databases = new Map([
    ['t68i-persistence', new Map([
        ['indexed-firmware', 'legacy-indexed-value'],
    ])],
])

globalThis.localStorage = {
    getItem(key) {
        return storage.get(key) ?? null
    },
    setItem(key, value) {
        storage.set(key, value)
    },
}

function databaseFor(name) {
    let values = databases.get(name)
    const database = {
        objectStoreNames: {
            contains() {
                return values !== undefined
            },
        },
        createObjectStore() {
            values = new Map()
            databases.set(name, values)
        },
        transaction() {
            return {
                error: null,
                objectStore() {
                    return {
                        get(key) {
                            const request = {}
                            queueMicrotask(() => {
                                request.result = values?.get(key)
                                request.onsuccess?.()
                            })
                            return request
                        },
                        put(value, key) {
                            const request = {}
                            queueMicrotask(() => {
                                values.set(key, value)
                                request.onsuccess?.()
                            })
                            return request
                        },
                    }
                },
            }
        },
    }
    return database
}

globalThis.indexedDB = {
    open(name) {
        const request = {}
        queueMicrotask(() => {
            request.result = databaseFor(name)
            if (!databases.has(name)) {
                request.onupgradeneeded?.()
            }
            request.onsuccess?.()
        })
        return request
    },
}

async function waitForStorage(key) {
    for (let attempt = 0; attempt < 20; attempt++) {
        const value = localStorage.getItem(key)
        if (value !== null) {
            return value
        }
        await new Promise(resolve => setImmediate(resolve))
    }
    assert.fail(`Timed out waiting for ${key}`)
}

test('Mia browser persistence migrates legacy storage', async () => {
    storage.set('t68i-persistence:local-firmware', 'legacy-local-value')

    assert.equal(loadText('local-firmware'), 'legacy-local-value')
    assert.equal(
        storage.get('mia-persistence:local-firmware'),
        'legacy-local-value')

    assert.equal(loadText('indexed-firmware'), null)
    assert.equal(
        await waitForStorage('mia-persistence:indexed-firmware'),
        'legacy-indexed-value')
    assert.equal(
        databases.get('mia-persistence').get('indexed-firmware'),
        'legacy-indexed-value')
})
