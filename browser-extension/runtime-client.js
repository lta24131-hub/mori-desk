// A transport error is not proof that a save failed. Never silently replay a
// write: the worker may have committed it before its response was interrupted.
globalThis.MoriRuntime = (() => {
  const readOnly = message => ['preferences', 'folders'].includes(message?.action);
  function failure(error, message) {
    const diagnostic = String(error?.message || error || '').slice(0, 500);
    if (/extension context invalidated|context has been invalidated/i.test(diagnostic)) {
      return {ok:false, code:'EXTENSION_RELOADED', error:'Nook 插件已更新，请刷新这个网页后继续收藏。', diagnostic};
    }
    if (readOnly(message)) {
      return {ok:false, code:'EXTENSION_UNAVAILABLE', error:'Nook 插件暂时没有响应，请稍后重试；仍无响应时刷新此网页。', diagnostic};
    }
    if (/receiving end does not exist|could not establish connection/i.test(diagnostic)) {
      return {ok:false, code:'EXTENSION_NO_RECEIVER', error:'Nook 插件连接尚未准备好，本次未发送收藏。请稍后重试。', diagnostic};
    }
    return {ok:false, code:'SAVE_RESULT_UNKNOWN', error:'未收到收藏结果，请先检查 Nook 图库，确认后再重试。', diagnostic};
  }
  function send(message, callback) {
    let finished = false, attempts = 0, timer;
    const finish = result => {
      if (finished) return;
      finished = true; clearTimeout(timer); callback(result);
    };
    const attempt = () => {
      if (finished) return;
      attempts++;
      let replied = false;
      try {
        chrome.runtime.sendMessage(message, response => {
          const error = chrome.runtime.lastError;
          if (finished || replied) return;
          replied = true;
          // Read requests may safely wake and retry a starting worker once.
          if (error && attempts === 1 && readOnly(message) && !/context.*invalidated/i.test(error.message || '')) {
            setTimeout(attempt, 150); return;
          }
          finish(error ? failure(error, message) : response && typeof response.ok === 'boolean' ? response : failure('The message returned no valid result', message));
        });
      } catch (error) {finish(failure(error, message));}
    };
    // A save may download for 20s, await the native host for 30s, then use a
    // paired local fallback for 20s after a definite pre-delivery host failure.
    // Keep this outer deadline beyond those stages; never resend the save.
    timer = setTimeout(() => finish(failure('Timed out waiting for the extension response', message)), message?.action === 'save' ? 75000 : 45000);
    attempt();
  }
  return {send, failure};
})();
