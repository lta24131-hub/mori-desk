// Window's native timers validate their receiver in Chromium/WebView2. Passing
// setTimeout directly to a class and later calling this.timer() binds the class
// as `this`, causing "Illegal invocation". Keep the browser owner at the actual
// call site; arrow wrappers are safe when stored or passed as callbacks.
export const scheduleTimer = (callback, delay) => globalThis.setTimeout(callback, delay);
export const cancelTimer = id => globalThis.clearTimeout(id);
