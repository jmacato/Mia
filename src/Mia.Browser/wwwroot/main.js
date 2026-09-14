import { prepareAudio, waitForAudio, bindAudioMemory } from './audio.js'

const progress = document.querySelector('.splash-progress')
const progressFill = document.querySelector('.splash-progress-fill')
const progressStatus = document.querySelector('.splash-status')
const tlsHelp = document.getElementById('tls-help')
const pageUrl = new URL(globalThis.location.href)
const pthreadPoolInitialSize = 4
const pthreadPoolStartupUnusedSize = 2
// Preload capacity for both the emulator and dedicated audio renderer. A
// synchronous request from the UI must not leave either waiting for the UI
// to create and register another browser worker.
const pthreadPoolIdleSize = 2
const usesThreadedRuntime = globalThis.crossOriginIsolated &&
    typeof globalThis.SharedArrayBuffer === 'function'

function terminateUnusedPthread(worker) {
    try {
        worker.terminate()
        worker.onmessage = () => {}
        return true
    } catch {
        return false
    }
}

function compactUnusedPthreads(pthreads, maximumLoadedWorkers) {
    const unusedWorkers = pthreads?.unusedWorkers
    if (!Array.isArray(unusedWorkers)) {
        return 0
    }

    let loadedCount = unusedWorkers.reduce(
        (count, worker) => count + (worker?.loaded === true ? 1 : 0),
        0)
    let terminated = 0
    while (loadedCount > maximumLoadedWorkers) {
        let candidateIndex = -1
        for (let index = unusedWorkers.length - 1; index >= 0; index--) {
            const worker = unusedWorkers[index]
            if (worker?.loaded === true &&
                Number(worker.info?.reuseCount) > 0) {
                candidateIndex = index
                break
            }
        }
        if (candidateIndex < 0) {
            for (let index = unusedWorkers.length - 1;
                index >= 0;
                index--) {
                if (unusedWorkers[index]?.loaded === true) {
                    candidateIndex = index
                    break
                }
            }
        }
        if (candidateIndex < 0) {
            break
        }

        const [worker] = unusedWorkers.splice(candidateIndex, 1)
        loadedCount--
        if (terminateUnusedPthread(worker)) {
            terminated++
        } else {
            unusedWorkers.splice(candidateIndex, 0, worker)
            loadedCount++
            break
        }
    }
    return terminated
}

function boundIdlePthreadPool(runtime) {
    const pthreads = runtime?.Module?.PThread
    const originalReturnWorker = pthreads?.returnWorkerToPool
    if (typeof originalReturnWorker !== 'function' ||
        originalReturnWorker.__calculatorIdleBound === true) {
        return 0
    }

    const boundedReturnWorker = worker => {
        const result = originalReturnWorker(worker)
        compactUnusedPthreads(pthreads, pthreadPoolIdleSize)
        return result
    }
    boundedReturnWorker.__calculatorIdleBound = true
    pthreads.returnWorkerToPool = boundedReturnWorker
    return compactUnusedPthreads(pthreads, pthreadPoolIdleSize)
}

function setProgress(value, status) {
    const percent = Math.round(Math.max(0, Math.min(1, value)) * 100)
    progressFill?.style.setProperty('width', `${percent}%`)
    progress?.setAttribute('aria-valuenow', String(percent))
    if (progressStatus && status) {
        progressStatus.textContent = status
    }
}

function dismissSplashWhenAvaloniaStarts() {
    const host = document.getElementById('out')
    const splash = document.getElementById('splash')
    if (!host || !splash) {
        return
    }
    const observer = new MutationObserver(() => {
        if (host.querySelector(':scope > canvas.avalonia-canvas')) {
            observer.disconnect()
            splash.remove()
        }
    })
    observer.observe(host, { childList: true })
}

async function boot() {
    const soundEnabled = !pageUrl.searchParams.has('unthrottled')
    const audioPreparation = soundEnabled ? prepareAudio() : Promise.resolve(true)
    setProgress(0.04, 'Loading WebAssembly runtime')
    const { dotnet } = await import('./_framework/dotnet.js')
    const runtimeConfig = usesThreadedRuntime
        ? {
            pthreadPoolInitialSize,
            pthreadPoolUnusedSize: pthreadPoolStartupUnusedSize,
        }
        : {}
    let runtimeBuilder = dotnet
        .withDiagnosticTracing(false)
        .withConfig(runtimeConfig)
    const runtime = await runtimeBuilder.create()
    bindAudioMemory(() => runtime.Module.HEAPU8.buffer)

    await audioPreparation
    if (soundEnabled && !pageUrl.searchParams.has('noauto') && !await prepareAudio()) {
        const controls = document.getElementById('audio-start')
        controls.hidden = false
        setProgress(0.35, 'Ready · start to enable sound')
        await Promise.race([
            waitForAudio(),
            new Promise(resolve => {
                document.getElementById('start-sound').addEventListener('click', async () => {
                    await prepareAudio()
                    resolve()
                }, { once: true })
            }),
        ])
        controls.hidden = true
    }
    setProgress(0.35, 'Loading firmware, GDFS, and ARM modem')
    dismissSplashWhenAvaloniaStarts()
    const config = runtime.getConfig()
    const args = [
        `--asset-base=${new URL('.', globalThis.location.href).href}`,
    ]
    if (new URLSearchParams(globalThis.location.search).has('diagnostics')) {
        args.push('--diagnostics')
    }
    if (new URLSearchParams(globalThis.location.search).has('unthrottled')) {
        args.push('--unthrottled')
    }
    if (new URLSearchParams(globalThis.location.search).has('noauto')) {
        args.push('--no-auto-start')
    }
    await runtime.runMain(config.mainAssemblyName, args)
    boundIdlePthreadPool(runtime)
}

try {
    await boot()
} catch (error) {
    const detail = error instanceof Error ? error.message : String(error)
    setProgress(1, `Load failed · ${detail}`)
    if (!globalThis.crossOriginIsolated && tlsHelp) {
        tlsHelp.hidden = false
    }
    console.error('Mia browser load failed:', error)
}
