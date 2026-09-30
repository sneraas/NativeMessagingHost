let nativePort = null;
let pendingResponses = [];

function getNativePort() {
    if (nativePort) {
        return nativePort;
    }

    nativePort = chrome.runtime.connectNative("no.starlims.localfs");

    nativePort.onMessage.addListener(response => {
        const resolve = pendingResponses.shift();

        if (resolve) {
            resolve(response);
        }
    });

    nativePort.onDisconnect.addListener(() => {
        nativePort = null;

        while (pendingResponses.length) {
            const resolve = pendingResponses.shift();

            resolve({
                ok: false,
                error: chrome.runtime.lastError?.message || "Native host disconnected."
            });
        }
    });

    return nativePort;
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    const port = getNativePort();

    pendingResponses.push(sendResponse);

    port.postMessage(message);

    return true;
});
