let context = null
let player = null
let initialization = null
let pendingResume = null
let pendingWorkletSamples = 0
let sourceSampleRate = 48000
let running = false
let unlocked = false
let recoveryTimer = 0
let fallbackEnabled = false
let fallbackNextStartTime = 0
let readWasmMemory = null
let sharedRing = null
const fallbackSources = new Set()
const maximumBufferedSeconds = 0.2

function unlockAudio() {
    unlocked = true
    void resumeContext(true)
    void ensureAudio()
}

function recoverAudio() {
    if (!unlocked || globalThis.document?.visibilityState === 'hidden') {
        return
    }
    if (recoveryTimer !== 0) {
        return
    }
    recoveryTimer = globalThis.setTimeout(() => {
        recoveryTimer = 0
        if (!unlocked || globalThis.document?.visibilityState === 'hidden') {
            return
        }
        void ensureAudio()
    }, 0)
}

function resetClosedContext() {
    if (context?.state !== 'closed') {
        return
    }
    context = null
    player = null
    initialization = null
    pendingResume = null
    pendingWorkletSamples = 0
    fallbackEnabled = false
    fallbackNextStartTime = 0
    fallbackSources.clear()
}

function ensureContext() {
    resetClosedContext()
    if (context !== null) {
        return context
    }

    const AudioContext = globalThis.AudioContext ?? globalThis.webkitAudioContext
    if (typeof AudioContext !== 'function') {
        return null
    }

    try {
        context = new AudioContext({ latencyHint: 'interactive' })
    } catch {
        context = new AudioContext()
    }
    context.addEventListener('statechange', () => {
        if (context?.state !== 'running') {
            recoverAudio()
        }
    })
    return context
}

function resumeContext(fromInteraction = false) {
    if (context === null || context.state === 'running') {
        return Promise.resolve(context !== null)
    }
    if (pendingResume !== null && !fromInteraction) {
        return pendingResume
    }
    const attempt = context.resume()
        .then(() => context?.state === 'running')
        .catch(() => false)
        .finally(() => {
            if (pendingResume === attempt) pendingResume = null
        })
    pendingResume = attempt
    return attempt
}

function promiseWithTimeout(promise, milliseconds, message) {
    let timer = 0
    const timeout = new Promise((_, reject) => {
        timer = globalThis.setTimeout(() => reject(new Error(message)), milliseconds)
    })
    return Promise.race([promise, timeout]).finally(() => globalThis.clearTimeout(timer))
}

function enableFallback() {
    fallbackEnabled = true
    // Disable the shared transport so the producer switches to the bounded
    // interop queue and wall-clock pacing if the worklet cannot run.
    if (sharedRing) Atomics.store(sharedRing.state, 2, 0)
    sharedRing = null
}

export function bindAudioMemory(getBuffer) {
    readWasmMemory = getBuffer
}

function configuration() {
    return { type: 'configure', sourceSampleRate, ring: sharedRing && {
        buffer: sharedRing.state.buffer,
        address: sharedRing.state.byteOffset,
        capacity: sharedRing.samples.length,
    } }
}

async function ensureAudio() {
    if (ensureContext() === null) {
        return null
    }

    void resumeContext()
    if (player !== null || fallbackEnabled) {
        return player
    }
    if (initialization !== null) {
        return initialization
    }
    if (!context.audioWorklet || typeof globalThis.AudioWorkletNode !== 'function') {
        enableFallback()
        return null
    }

    initialization = (async () => {
        await promiseWithTimeout(
            context.audioWorklet.addModule(
                new URL('./audio-worklet.js', import.meta.url)),
            2_000,
            'AudioWorklet startup timed out')
        const worklet = new AudioWorkletNode(context, 'mia-pcm-player', {
            numberOfInputs: 0,
            numberOfOutputs: 1,
            outputChannelCount: [1],
            processorOptions: { sourceSampleRate },
        })
        // The emulator mixes all sources into this PCM stream. There is one
        // device output, with no media-element route or competing mixer.
        worklet.connect(context.destination)
        worklet.port.onmessage = event => {
            if (player === worklet && event.data?.type === 'received') {
                pendingWorkletSamples = Math.max(0,
                    pendingWorkletSamples - event.data.samples)
            }
        }
        worklet.addEventListener('processorerror', () => {
            if (player !== worklet) return
            worklet.disconnect()
            player = null
            pendingWorkletSamples = 0
            enableFallback()
        })
        worklet.port.postMessage(configuration())
        player = worklet
        return player
    })().catch(error => {
        console.warn('Mia AudioWorklet could not start; using buffered PCM output', error)
        enableFallback()
        return null
    }).finally(() => {
        initialization = null
    })
    return initialization
}

function clearFallback() {
    for (const source of fallbackSources) {
        source.onended = null
        try {
            source.stop()
        } catch {
        }
        source.disconnect()
    }
    fallbackSources.clear()
    fallbackNextStartTime = context?.currentTime ?? 0
}

function enqueueFallback(bytes) {
    if (context === null || !fallbackEnabled || !running) {
        return
    }

    const signed = new Int16Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 2)
    if (signed.length === 0) {
        return
    }
    const samples = new Float32Array(signed.length)
    for (let index = 0; index < signed.length; index++) {
        samples[index] = signed[index] / 32768
    }

    const buffer = context.createBuffer(1, samples.length, sourceSampleRate)
    buffer.copyToChannel(samples, 0)
    const source = context.createBufferSource()
    source.buffer = buffer
    source.connect(context.destination)
    const now = context.currentTime
    if (fallbackNextStartTime - now > maximumBufferedSeconds) {
        clearFallback()
    }
    const startTime = Math.max(fallbackNextStartTime, now + 0.015)
    fallbackNextStartTime = startTime + buffer.duration
    fallbackSources.add(source)
    source.onended = () => {
        fallbackSources.delete(source)
        source.disconnect()
    }
    source.start(startTime)
}

// Autoplay permission can allow audio immediately. Otherwise capture a trusted
// interaction before Avalonia handles it, and recover after page suspension.
for (const type of ['pointerdown', 'touchstart', 'keydown']) {
    globalThis.addEventListener(type, unlockAudio, { capture: true, passive: true })
}
globalThis.addEventListener('focus', recoverAudio, { passive: true })
globalThis.addEventListener('pageshow', recoverAudio, { passive: true })
globalThis.document?.addEventListener('visibilitychange', recoverAudio, { passive: true })

export async function prepareAudio() {
    await ensureAudio()
    if (context === null) return true
    const ready = await promiseWithTimeout(resumeContext(), 250, 'Autoplay blocked')
        .catch(() => false)
    if (ready) unlocked = true
    return ready
}

export function waitForAudio() {
    if (context === null || context.state === 'running') return Promise.resolve()
    return new Promise(resolve => {
        const changed = () => {
            if (context?.state !== 'running') return
            context.removeEventListener('statechange', changed)
            unlocked = true
            resolve()
        }
        context.addEventListener('statechange', changed)
        changed()
    })
}

export function start(sampleRate, ringAddress = 0, ringCapacity = 0) {
    if (Number.isFinite(sampleRate) && sampleRate > 0) {
        sourceSampleRate = Math.round(sampleRate)
    }
    sharedRing = null
    const memory = readWasmMemory?.()
    if (!fallbackEnabled && typeof SharedArrayBuffer === 'function' && memory instanceof SharedArrayBuffer &&
        ringAddress > 0 && ringAddress % 4 === 0 && ringCapacity > 0 &&
        (ringCapacity & (ringCapacity - 1)) === 0 &&
        ringAddress + 16 + ringCapacity * 2 <= memory.byteLength) {
        sharedRing = {
            state: new Int32Array(memory, ringAddress, 4),
            samples: new Int16Array(memory, ringAddress + 16, ringCapacity),
        }
    }
    running = true
    player?.port.postMessage(configuration())
    void ensureAudio()
    return sharedRing !== null
}

export function pushSamples(bytes) {
    if (!running || context?.state !== 'running' ||
        !bytes || bytes.length === 0 || (bytes.length % 2) !== 0) {
        return
    }
    const sampleCount = bytes.length / 2
    if (sampleCount > sourceSampleRate * maximumBufferedSeconds ||
        player !== null && pendingWorkletSamples + sampleCount >
            sourceSampleRate * maximumBufferedSeconds) {
        return
    }

    // A MemoryView points into WebAssembly memory and is valid only for this
    // call. It has no numeric array properties; copy its little-endian PCM
    // through the interop API before handing data to asynchronous JS.
    const copy = new Uint8Array(bytes.length)
    bytes.copyTo(copy)
    if (player !== null) {
        const samples = new Int16Array(copy.buffer)
        pendingWorkletSamples += samples.length
        player.port.postMessage({ type: 'samples', samples }, [samples.buffer])
        return
    }
    if (fallbackEnabled) {
        enqueueFallback(copy)
        return
    }
    void ensureAudio()
}

export function stop() {
    running = false
    player?.port.postMessage({ type: 'clear' })
    clearFallback()
    sharedRing = null
}
