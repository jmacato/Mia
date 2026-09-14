function copyTransferBytes(bytes) {
    if (!bytes || !Number.isSafeInteger(bytes.length) || bytes.length < 0) {
        throw new TypeError('Transfer data is not a valid byte memory view.')
    }

    // The managed MemoryView can point into SharedArrayBuffer-backed WebAssembly
    // memory and is valid only for the duration of the interop call. Blob must
    // receive an owned copy. The wrapper exposes copyTo, not array indices.
    const copy = new Uint8Array(bytes.length)
    bytes.copyTo(copy)
    return copy
}

export function downloadTransferFile(name, mediaType, bytes) {
    const copy = copyTransferBytes(bytes)
    const blob = new Blob([copy], {
        type: mediaType || 'application/octet-stream',
    })
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = name || 'transfer.bin'
    anchor.hidden = true
    document.body.append(anchor)
    try {
        anchor.click()
    } finally {
        anchor.remove()
        queueMicrotask(() => URL.revokeObjectURL(url))
    }
}
