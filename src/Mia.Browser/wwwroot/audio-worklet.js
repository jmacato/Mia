const MAXIMUM_BUFFER_SECONDS = 0.2
const START_BUFFER_SECONDS = 0.06

class MiaPcmPlayer extends AudioWorkletProcessor {
    constructor(options) {
        super()
        this.sourceSampleRate = this.readSampleRate(
            options.processorOptions?.sourceSampleRate)
        this.maximumQueuedSamples = Math.max(
            1,
            Math.ceil(this.sourceSampleRate * MAXIMUM_BUFFER_SECONDS))
        this.chunks = []
        this.headOffset = 0
        this.queuedSamples = 0
        this.currentSample = 0
        this.hasCurrentSample = false
        this.resamplePhase = 0
        this.buffering = true
        this.ring = null
        this.port.onmessage = event => this.receive(event.data)
    }

    readSampleRate(value) {
        return Number.isFinite(value) && value > 0
            ? Math.round(value)
            : 48000
    }

    receive(message) {
        switch (message?.type) {
            case 'configure':
                this.ring = message.ring ? {
                    state: new Int32Array(message.ring.buffer, message.ring.address, 4),
                    samples: new Int16Array(message.ring.buffer, message.ring.address + 16,
                        message.ring.capacity),
                } : null
                this.sourceSampleRate = this.readSampleRate(
                    message.sourceSampleRate)
                this.maximumQueuedSamples = Math.max(
                    1,
                    Math.ceil(
                        this.sourceSampleRate * MAXIMUM_BUFFER_SECONDS))
                this.clear()
                break
            case 'samples':
                this.enqueue(message.samples)
                this.port.postMessage({ type: 'received', samples: message.samples.length })
                break
            case 'clear':
                this.clear()
                break
        }
    }

    enqueue(samples) {
        if (!(samples instanceof Int16Array) || samples.length === 0) {
            return
        }
        if (samples.length > this.maximumQueuedSamples) {
            samples = samples.subarray(
                samples.length - this.maximumQueuedSamples)
        }
        this.chunks.push(samples)
        this.queuedSamples += samples.length

        let dropped = false
        while (this.queuedSamples > this.maximumQueuedSamples) {
            const excess = this.queuedSamples - this.maximumQueuedSamples
            const available = this.chunks[0].length - this.headOffset
            const count = Math.min(excess, available)
            this.headOffset += count
            this.queuedSamples -= count
            dropped = true
            if (this.headOffset === this.chunks[0].length) {
                this.chunks.shift()
                this.headOffset = 0
            }
        }
        if (dropped) {
            this.hasCurrentSample = false
            this.resamplePhase = 0
        }
    }

    dequeue() {
        if (this.queuedSamples === 0) {
            return null
        }
        if (this.ring) {
            this.queuedSamples--
            return this.ring.samples[this.ringCursor++ & (this.ring.samples.length - 1)]
        }
        const value = this.chunks[0][this.headOffset++]
        this.queuedSamples--
        if (this.headOffset === this.chunks[0].length) {
            this.chunks.shift()
            this.headOffset = 0
        }
        return value
    }

    clear() {
        this.chunks.length = 0
        this.headOffset = 0
        this.queuedSamples = 0
        this.currentSample = 0
        this.hasCurrentSample = false
        this.resamplePhase = 0
        this.buffering = true
    }

    process(_inputs, outputs) {
        const output = outputs[0]?.[0]
        if (!output) {
            return true
        }
        output.fill(0)
        let read = 0
        let generation = 0
        if (this.ring) {
            const state = this.ring.state
            if (Atomics.load(state, 2) === 0) {
                this.clear()
                return true
            }
            generation = Atomics.load(state, 3)
            read = Atomics.load(state, 1)
            if (this.ringGeneration !== generation || this.ringCursor !== read) {
                this.clear()
                this.ringGeneration = generation
            }
            this.ringCursor = read
            this.queuedSamples = (Atomics.load(state, 0) - read) >>> 0
        }
        // Build a small reserve before playback (and after an underrun), so
        // individual emulator batches do not alternate between PCM and silence.
        if (this.buffering) {
            if (this.queuedSamples < this.sourceSampleRate * START_BUFFER_SECONDS) {
                return true
            }
            this.buffering = false
        }

        for (let i = 0; i < output.length; i++) {
            if (!this.hasCurrentSample) {
                const first = this.dequeue()
                if (first === null) {
                    this.buffering = true
                    break
                }
                this.currentSample = first
                this.hasCurrentSample = true
            }

            output[i] = this.currentSample / 32768
            this.resamplePhase += this.sourceSampleRate
            while (this.resamplePhase >= sampleRate) {
                const next = this.dequeue()
                if (next === null) {
                    this.hasCurrentSample = false
                    this.resamplePhase = 0
                    this.buffering = true
                    break
                }
                this.currentSample = next
                this.resamplePhase -= sampleRate
            }
        }
        if (this.ring) {
            const state = this.ring.state
            if (Atomics.load(state, 3) !== generation ||
                Atomics.compareExchange(state, 1, read, this.ringCursor | 0) !== read) {
                output.fill(0)
                this.clear()
            }
            this.ringCursor |= 0
        }
        return true
    }
}

registerProcessor('mia-pcm-player', MiaPcmPlayer)
