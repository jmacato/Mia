const databaseName = 'mia-persistence'
const legacyDatabaseName = 't68i-persistence'
const storeName = 'snapshots'
const databaseVersion = 1
const localStoragePrefix = `${databaseName}:`
const legacyLocalStoragePrefix = `${legacyDatabaseName}:`

const databasePromises = new Map()

function openDatabase(name) {
    const existing = databasePromises.get(name)
    if (existing) {
        return existing
    }

    const promise = new Promise((resolve, reject) => {
        const request = indexedDB.open(name, databaseVersion)

        request.onupgradeneeded = () => {
            if (!request.result.objectStoreNames.contains(storeName)) {
                request.result.createObjectStore(storeName)
            }
        }

        request.onsuccess = () => resolve(request.result)
        request.onerror = () => reject(
            request.error ?? new Error('Failed to open IndexedDB'))
        request.onblocked = () => reject(
            new Error('IndexedDB upgrade was blocked'))
    })
    databasePromises.set(name, promise)

    return promise
}

async function loadTextFromDatabase(name, key) {
    const database = await openDatabase(name)
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readonly')
        const request = transaction.objectStore(storeName).get(key)
        request.onsuccess = () => resolve(request.result ?? null)
        request.onerror = () => reject(
            request.error ?? new Error('Failed to load persistence snapshot'))
    })
}

async function migrateTextFromDatabases(key, storageKey) {
    for (const name of [databaseName, legacyDatabaseName]) {
        const databaseValue = await loadTextFromDatabase(name, key)
        if (databaseValue !== null && localStorage.getItem(storageKey) === null) {
            localStorage.setItem(storageKey, databaseValue)
            if (name === legacyDatabaseName) {
                await mirrorTextToDatabase(key, databaseValue)
            }
            console.info(
                'Mia persistence migrated from IndexedDB; reload to use it.')
            return
        }
    }
}

export function loadText(key) {
    const storageKey = `${localStoragePrefix}${key}`
    const localValue = localStorage.getItem(storageKey)
    if (localValue !== null) {
        return localValue
    }

    const legacyValue = localStorage.getItem(`${legacyLocalStoragePrefix}${key}`)
    if (legacyValue !== null) {
        localStorage.setItem(storageKey, legacyValue)
        void mirrorTextToDatabase(key, legacyValue).catch(error => {
            console.error('Mia IndexedDB persistence migration failed:', error)
        })
        return legacyValue
    }

    void migrateTextFromDatabases(key, storageKey).catch(error => {
        console.error('Mia IndexedDB persistence migration failed:', error)
    })
    return null
}

async function mirrorTextToDatabase(key, value) {
    const database = await openDatabase(databaseName)

    await new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readwrite')
        const request = transaction.objectStore(storeName).put(value, key)
        request.onsuccess = () => resolve()
        request.onerror = () => reject(
            request.error ?? new Error('Failed to save persistence snapshot'))
        transaction.onerror = () => reject(
            transaction.error ?? new Error('IndexedDB transaction failed'))
    })
}

export function saveText(key, value) {
    localStorage.setItem(`${localStoragePrefix}${key}`, value)
    void mirrorTextToDatabase(key, value).catch(error => {
        console.error('Mia IndexedDB persistence mirror failed:', error)
    })
}
