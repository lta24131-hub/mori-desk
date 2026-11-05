// Desktop feedback belongs to the save operation, not the lifetime of its page.
// A notification failure must never change or repeat the actual save.
globalThis.MoriDesktopFeedback = {
  create({send, uuid}) {
    const unknown = new Set(['NATIVE_TIMEOUT','NATIVE_EMPTY_REPLY','NATIVE_HOST_DISCONNECTED','NATIVE_CONNECTION_ERROR','LOCAL_CONNECTION_UNAVAILABLE','SAVE_RESULT_UNKNOWN']);
    const text = (value, max) => typeof value === 'string' ? value.slice(0, max) : '';
    const deliver = async value => {try {return (await send({action:'collectionFeedback', ...value}))?.ok === true;} catch {return false;}};
    return {
      async collect(message, task, started = () => {}) {
        const id = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(message.feedbackId || '') ? message.feedbackId : uuid(), name = text(message.name,250) || '网页收藏';
        const preview = /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(message.feedbackPreview || '') && message.feedbackPreview.length <= 90000 ? message.feedbackPreview : '';
        const base = {id, name, preview, folderName:text(message.folderName,250),theme:message.feedbackTheme==='dark'?'dark':'light'};
        const pending = deliver({...base,state:'saving'}).then(ok => {if(ok)try{started();}catch{} return ok;});
        let result;
        try {result = await task();} catch {result={ok:false,code:'SAVE_RESULT_UNKNOWN',error:'收藏结果未确认，请先检查图库，避免重复收藏。'};}
        // Preserve ordering even when a cached image saves during a cold start.
        // A failed progress channel is not a reason to launch another native
        // host and make an already finished save wait through a second timeout.
        if (!await pending) return {...result,desktopFeedback:false};
        const state = result.choiceRequired ? 'choice' : result.ok ? 'success' : unknown.has(result.code) ? 'unknown' : 'error';
        const desktopFeedback = await deliver({...base,state,name:text(result.name,250)||name,
          detail:text(result.error,600),duplicate:result.duplicate===true,keptCopy:result.keptCopy===true});
        return {...result,desktopFeedback};
      }
    };
  }
};
