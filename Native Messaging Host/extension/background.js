chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    const port = chrome.runtime.connectNative("no.test.starlims.localfs");

    port.onMessage.addListener(response => {
        sendResponse(response);
        port.disconnect();
    });

    port.onDisconnect.addListener(() => {
        if (chrome.runtime.lastError) {
            sendResponse({
                ok: false,
                error: chrome.runtime.lastError.message
            });
        }
    });

    port.postMessage(message);
    return true;
});