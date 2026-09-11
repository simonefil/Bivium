// Shared circuit lifecycle: workspace presence precedes terminal recovery.
let connected = true;
let generation = 0;
let recovery = null;
const participants = new Set();

export function isCircuitConnected() {
    return connected;
}

export function registerCircuitParticipant(participant) {
    participants.add(participant);
    if (!connected) participant.suspend?.();
    return () => participants.delete(participant);
}

export function suspendCircuit() {
    connected = false;
    generation++;
    recovery = null;
    for (const participant of participants) participant.suspend?.();
}

// Async also captures a synchronous SignalR send failure as a handled Promise.
export async function invokeCircuitMethod(reference, method, ...args) {
    if (!connected || !reference) return null;
    return await reference.invokeMethodAsync(method, ...args);
}

export function recoverCircuit() {
    if (connected) return Promise.resolve(true);
    if (recovery) return recovery;
    const currentGeneration = generation;
    const isCurrent = () => generation === currentGeneration;
    recovery = (async function () {
        let timeout;
        try {
            await Promise.race([
                (async function () {
                    for (const phase of ['presence', 'terminal']) {
                        for (const participant of participants) {
                            if (!isCurrent()) return;
                            if (participant.phase === phase) await participant.recover(isCurrent);
                        }
                    }
                })(),
                new Promise((_, reject) => {
                    timeout = setTimeout(() => reject(new Error('Circuit recovery timed out')), 10000);
                })
            ]);
            if (!isCurrent()) return false;
            connected = true;
            return true;
        } catch (error) {
            if (!isCurrent()) return false;
            // Invalidate continuations even when an interop call outlives the timeout.
            generation++;
            recovery = null;
            throw error;
        } finally {
            clearTimeout(timeout);
            if (isCurrent()) recovery = null;
        }
    })();
    return recovery;
}
