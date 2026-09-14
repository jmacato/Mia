import assert from 'node:assert/strict'
import { setImmediate } from 'node:timers/promises'
import test from 'node:test'
import { readFile } from 'node:fs/promises'
import vm from 'node:vm'

globalThis.addEventListener = () => {}

const messages = []
globalThis.AudioContext = class {
    state = 'running'
    currentTime = 0
    destination = {}
    audioWorklet = { addModule: async () => {} }

    addEventListener() {}

    createGain() {
        return { connect() {} }
    }
}
globalThis.AudioWorkletNode = class {
    addEventListener() {}
    port = {
        postMessage(message, transfer = []) {
            messages.push(structuredClone(message, { transfer }))
        },
    }

    connect() {}
}

test('managed audio preserves signed PCM after the interop view expires', async () => {
    const audio = await import('../src/Mia.Browser/wwwroot/audio.js')
    audio.start(48000)
    await setImmediate()

    const expected = [0, 8192, -8192, 32767, -32768]
    const source = new Uint8Array(new SharedArrayBuffer(32), 8, expected.length * 2)
    new Int16Array(source.buffer, source.byteOffset, expected.length).set(expected)
    let disposed = false
    // .NET Span exposes length/copyTo, without numeric array properties.
    const view = {
        length: source.length,
        copyTo(destination) {
            assert.equal(disposed, false)
            destination.set(source)
        },
    }

    audio.pushSamples(view)
    disposed = true
    source.fill(0)
    audio.stop()

    const chunks = messages.filter(message => message.type === 'samples')
    assert.equal(chunks.length, 1)
    assert.deepEqual([...chunks[0].samples], expected)
})

test('autoplay permission starts audio without waiting for an input event', async () => {
    const audio = await import('../src/Mia.Browser/wwwroot/audio.js')
    assert.equal(await audio.prepareAudio(), true)
})

test('mixed PCM has one direct output across restarts and worklet fallback', async () => {
    let contexts = 0
    let worklet
    const destination = {}
    const connected = new Set()
    const document = globalThis.document
    globalThis.document = {
        addEventListener() {},
        createElement() { assert.fail('Audio must not create a media element') },
    }
    globalThis.AudioContext = class {
        constructor() { contexts++ }
        state = 'running'
        currentTime = 0
        destination = destination
        audioWorklet = { addModule: async () => {} }
        addEventListener() {}
        createMediaStreamDestination() { assert.fail('Audio must use the device output') }
        createBuffer(_channels, length, rate) {
            return { duration: length / rate, copyToChannel() {} }
        }
        createBufferSource() {
            return {
                connect(target) { assert.equal(target, destination); connected.add(this) },
                disconnect() { connected.delete(this) },
                start() {}, stop() {},
            }
        }
    }
    globalThis.AudioWorkletNode = class {
        constructor() { worklet = this }
        port = { postMessage() {} }
        addEventListener(type, callback) { this[type] = callback }
        connect(target) { assert.equal(target, destination); connected.add(this) }
        disconnect() { connected.delete(this) }
    }
    try {
        const audio = await import('../src/Mia.Browser/wwwroot/audio.js?direct-output-test')
        assert.equal(await audio.prepareAudio(), true)
        for (let i = 0; i < 3; i++) {
            audio.start(48000)
            audio.stop()
        }
        assert.equal(contexts, 1)
        assert.deepEqual([...connected], [worklet])
        const memory = new SharedArrayBuffer(65536)
        const state = new Int32Array(memory, 16, 4)
        state.set([0, 0, 1, 1])
        audio.bindAudioMemory(() => memory)
        assert.equal(audio.start(48000, 16, 16384), true)
        worklet.processorerror()
        assert.equal(connected.size, 0)
        assert.equal(Atomics.load(state, 2), 0)
        audio.start(48000)
        audio.pushSamples({ length: 1920, copyTo: copy => copy.fill(1) })
        assert.equal(connected.size, 1)
        assert.equal(connected.has(worklet), false)
        assert.equal(contexts, 1)
        audio.stop()
        assert.equal(connected.size, 0)
    } finally {
        globalThis.document = document
    }
})

test('blocked autoplay stays bounded and a later gesture enables audio', async () => {
    const listeners = new Map()
    globalThis.addEventListener = (type, listener) => listeners.set(type, listener)
    let allowed = false
    let resumes = 0
    let modules = 0
    let worklet
    globalThis.AudioContext = class extends EventTarget {
        state = 'suspended'
        currentTime = 0
        destination = {}
        audioWorklet = { addModule: async () => { modules++ } }
        createGain() { return { connect() {} } }
        resume() {
            resumes++
            if (!allowed) return new Promise(() => {})
            this.state = 'running'
            this.dispatchEvent(new Event('statechange'))
            return Promise.resolve()
        }
    }
    const delivered = []
    globalThis.AudioWorkletNode = class {
        constructor() { worklet = this }
        port = { postMessage(message) { delivered.push(message) } }
        addEventListener() {}
        connect() {}
    }
    const audio = await import('../src/Mia.Browser/wwwroot/audio.js?autoplay-test')
    assert.equal(await audio.prepareAudio(), false)
    audio.start(48000)
    const view = { length: 1920, copyTo: copy => copy.fill(1) }
    for (let i = 0; i < 10000; i++) audio.pushSamples(view)
    assert.equal(resumes, 1)
    assert.equal(modules, 1)
    assert.equal(delivered.filter(x => x.type === 'samples').length, 0)

    const ready = audio.waitForAudio()
    allowed = true
    listeners.get('pointerdown')()
    await ready
    await setImmediate()
    // A worklet which stops consuming its MessagePort cannot retain unlimited PCM.
    for (let i = 0; i < 10000; i++) audio.pushSamples(view)
    assert.equal(delivered.filter(x => x.type === 'samples').length, 10)
    worklet.port.onmessage({ data: { type: 'received', samples: 9600 } })
    audio.pushSamples(view)
    assert.equal(delivered.filter(x => x.type === 'samples').length, 11)
    audio.stop()
})

test('worklet acknowledges delivery and bounds audio when rendering is stalled', async () => {
    let Player
    const replies = []
    const sandbox = {
        sampleRate: 48000, Int16Array,
        AudioWorkletProcessor: class {
            port = { postMessage: message => replies.push(message) }
        },
        registerProcessor(_name, implementation) { Player = implementation },
    }
    vm.runInNewContext(await readFile(
        new URL('../src/Mia.Browser/wwwroot/audio-worklet.js', import.meta.url), 'utf8'), sandbox)
    const player = new Player({ processorOptions: { sourceSampleRate: 48000 } })
    for (let i = 0; i < 10000; i++) {
        player.receive({ type: 'samples', samples: new Int16Array(960).fill(8192) })
    }
    assert.equal(player.queuedSamples, 9600)
    assert.equal(replies.length, 10000)
    assert.equal(replies[0].samples, 960)
    const output = new Float32Array(128)
    player.process([], [[output]])
    assert.equal(output[0], 0.25)
})

test('worklet plays unevenly delivered chunks continuously at device sample rates', async () => {
    const source = await readFile(
        new URL('../src/Mia.Browser/wwwroot/audio-worklet.js', import.meta.url), 'utf8')
    for (const rate of [44100, 48000]) {
        let Player
        vm.runInNewContext(source, {
            sampleRate: rate, Int16Array,
            AudioWorkletProcessor: class { port = { postMessage() {} } },
            registerProcessor(_name, implementation) { Player = implementation },
        })
        const player = new Player({ processorOptions: { sourceSampleRate: 48000 } })
        const arrivals = []
        for (let i = 0; i < 100; i++) {
            arrivals.push(Math.max(arrivals.at(-1) ?? 0,
                i * 20 + [0, 12, -7, 8, 25, -3][i % 6]))
        }
        const played = []
        let next = 0
        for (let frame = 0; frame < Math.ceil(rate * 2.2 / 128); frame++) {
            const time = frame * 128 * 1000 / rate
            while (next < arrivals.length && arrivals[next] <= time) {
                player.receive({ type: 'samples', samples: new Int16Array(960).fill(8192) })
                next++
            }
            const output = new Float32Array(128)
            player.process([], [[output]])
            played.push(...output)
            assert.ok(player.queuedSamples <= 9600)
        }
        const first = played.indexOf(0.25)
        const last = played.lastIndexOf(0.25)
        assert.ok(first >= 0)
        assert.ok(played.slice(first, last + 1).every(x => x === 0.25),
            `${rate} Hz output must not insert silence between late chunks`)
        assert.ok(Math.abs(last - first + 1 - rate * 2) <= 1)

        // After a real stall, wait for a reserve again rather than stuttering
        // through each newly arriving chunk. Clear discards that reserve too.
        player.receive({ type: 'samples', samples: new Int16Array(960).fill(8192) })
        const output = new Float32Array(128)
        player.process([], [[output]])
        assert.ok(output.every(x => x === 0))
        assert.equal(player.queuedSamples, 960)
        player.receive({ type: 'clear' })
        assert.equal(player.queuedSamples, 0)
    }
})

test('worklet pulls shared PCM without UI messages, including wrap and memory growth', async () => {
    const source = await readFile(
        new URL('../src/Mia.Browser/wwwroot/audio-worklet.js', import.meta.url), 'utf8')
    for (const rate of [44100, 48000]) {
        let Player
        vm.runInNewContext(source, {
            sampleRate: rate, Int16Array, Int32Array, Atomics,
            AudioWorkletProcessor: class { port = { postMessage() {} } },
            registerProcessor(_name, implementation) { Player = implementation },
        })
        const memory = new WebAssembly.Memory({ initial: 1, maximum: 2, shared: true })
        const state = new Int32Array(memory.buffer, 0, 4)
        const samples = new Int16Array(memory.buffer, 16, 16384)
        // Cross the signed and unsigned counter boundaries during playback.
        let write = -50000
        state.set([write, write, 1, 1])
        const player = new Player({ processorOptions: { sourceSampleRate: 48000 } })
        player.receive({ type: 'configure', sourceSampleRate: 48000,
            ring: { buffer: memory.buffer, address: 0, capacity: samples.length } })
        memory.grow(1)
        const played = []
        let chunks = 0
        for (let frame = 0; frame < Math.ceil(rate * 2.2 / 128); frame++) {
            const milliseconds = frame * 128 * 1000 / rate
            while (chunks < 100 && milliseconds >= chunks * 20) {
                for (let i = 0; i < 960; i++) samples[(write + i) & 16383] = 8192
                write = (write + 960) | 0
                Atomics.store(state, 0, write)
                chunks++
            }
            const output = new Float32Array(128)
            player.process([], [[output]])
            played.push(...output)
        }
        const first = played.indexOf(0.25), last = played.lastIndexOf(0.25)
        assert.ok(first >= 0)
        assert.ok(played.slice(first, last + 1).every(x => x === 0.25))
        assert.ok(Math.abs(last - first + 1 - rate * 2) <= 1)
        assert.equal(Atomics.load(state, 1), write)
        assert.equal(player.chunks.length, 0)
        Atomics.store(state, 2, 0)
        const output = new Float32Array(128)
        player.process([], [[output]])
        assert.ok(output.every(x => x === 0))
    }
})

test('shared overflow invalidates an in-flight read instead of playing overwritten PCM', async () => {
    let Player
    vm.runInNewContext(await readFile(
        new URL('../src/Mia.Browser/wwwroot/audio-worklet.js', import.meta.url), 'utf8'), {
        sampleRate: 48000, Int16Array, Int32Array, Atomics,
        AudioWorkletProcessor: class { port = { postMessage() {} } },
        registerProcessor(_name, implementation) { Player = implementation },
    })
    const buffer = new SharedArrayBuffer(16 + 16384 * 2)
    const state = new Int32Array(buffer, 0, 4)
    const samples = new Int16Array(buffer, 16, 16384)
    state.set([9600, 0, 1, 1])
    samples.fill(8192)
    const player = new Player({ processorOptions: { sourceSampleRate: 48000 } })
    player.receive({ type: 'configure', sourceSampleRate: 48000,
        ring: { buffer, address: 0, capacity: samples.length } })
    const dequeue = player.dequeue.bind(player)
    let count = 0
    player.dequeue = () => {
        if (++count === 64) {
            Atomics.store(state, 1, 960)
            Atomics.store(state, 0, 10560)
        }
        return dequeue()
    }
    const output = new Float32Array(128)
    player.process([], [[output]])
    assert.ok(output.every(x => x === 0))
    assert.equal(Atomics.load(state, 1), 960)
    player.process([], [[output]])
    assert.ok(output.every(x => x === 0.25))
    assert.equal(Atomics.load(state, 1), 1089)
})
