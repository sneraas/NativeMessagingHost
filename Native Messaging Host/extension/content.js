console.log("STARLIMS LocalFS content script loaded");

window.addEventListener("message", event => {
    if (event.source !== window) return;
    if (event.data?.source !== "STARLIMS_LOCAL_TEST") return;

    chrome.runtime.sendMessage(event.data, response => {
        window.postMessage({
            source: "STARLIMS_LOCAL_TEST_RESPONSE",
            requestId: event.data.requestId,
            response
        }, "*");
    });
});