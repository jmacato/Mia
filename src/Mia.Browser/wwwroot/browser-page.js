export async function fetchImage(url) {
    const response = await fetch(url)
    if (!response.ok) {
        throw new Error(`Could not load image: HTTP ${response.status}`)
    }
    return new Uint8Array(await response.arrayBuffer())
}

export function copyImage(image, destination) {
    destination.set(image)
}

export function reloadForEmulatorRestart() {
    const target = new URL(globalThis.location.href)
    target.searchParams.delete('noauto')
    globalThis.location.replace(target.href)
}
