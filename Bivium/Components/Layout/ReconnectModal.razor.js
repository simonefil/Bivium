import { recoverCircuit, suspendCircuit } from '../../js/connection.js';

const reconnectModal = document.getElementById('components-reconnect-modal');
const retryButton = document.getElementById('components-reconnect-button');
const resumeButton = document.getElementById('components-resume-button');
let retryPending = false;
let retryTimer = null;
let reconnectState = 'hide';

reconnectModal.addEventListener('components-reconnect-state-changed', handleReconnectStateChanged);
retryButton.addEventListener('click', retry);
resumeButton.addEventListener('click', resume);
window.addEventListener('offline', suspendCircuit);
window.addEventListener('online', retryWhenAvailable);
document.addEventListener('visibilitychange', retryWhenAvailable);

function clearRetryTimer() {
    if (retryTimer !== null) clearTimeout(retryTimer);
    retryTimer = null;
}

function scheduleRetry() {
    clearRetryTimer();
    if (reconnectState === 'failed') retryTimer = setTimeout(retry, 5000);
}

function handleReconnectStateChanged(event) {
    reconnectState = event.detail.state;
    clearRetryTimer();
    if (reconnectState === 'hide') {
        void completeRecovery();
        return;
    }

    suspendCircuit();
    if (!reconnectModal.open) reconnectModal.showModal();
    if (reconnectState === 'rejected') location.reload();
    if (reconnectState === 'failed') scheduleRetry();
}

async function completeRecovery() {
    try {
        // Keep the existing reconnect UI visible until the terminal handoff has completed.
        reconnectModal.classList.add('components-reconnect-show');
        if (await recoverCircuit()) {
            reconnectState = 'hide';
            clearRetryTimer();
            reconnectModal.classList.remove('components-reconnect-show');
            reconnectModal.close();
        }
    } catch {
        // A live circuit with unusable interop must reattach through a fresh page.
        location.reload();
    }
}

async function retry() {
    if (retryPending) return;
    clearRetryTimer();
    retryPending = true;
    try {
        const successful = await Blazor.reconnect();
        if (successful) {
            await completeRecovery();
        } else {
            const resumed = await Blazor.resumeCircuit();
            if (resumed) await completeRecovery();
            else location.reload();
        }
    } catch {
        reconnectState = 'failed';
        scheduleRetry();
    } finally {
        retryPending = false;
    }
}

async function resume() {
    if (retryPending) return;
    retryPending = true;
    try {
        if (await Blazor.resumeCircuit()) await completeRecovery();
        else location.reload();
    } catch {
        reconnectModal.classList.replace('components-reconnect-paused', 'components-reconnect-resume-failed');
    } finally {
        retryPending = false;
    }
}

function retryWhenAvailable() {
    if (document.visibilityState === 'visible' && reconnectState === 'failed') void retry();
    else if (reconnectState === 'hide') void completeRecovery();
}
