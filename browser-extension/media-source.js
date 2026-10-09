(() => {
  // Resolve only the video the user chose. No background polling, cookies API,
  // private API calls, third-party download service or stream recording.
  const MAX_HTML = 8 * 1024 * 1024;
  const MAX_VIDEO = 20 * 1024 * 1024;
  const isInstagram = host => host === 'instagram.com' || host === 'www.instagram.com';
  function reel(value) {
    try {
      const url = new URL(value);
      const match = url.pathname.match(/^\/(?:[A-Za-z0-9._]{1,30}\/)?(?:reels?|p)\/([A-Za-z0-9_-]{5,64})\/?$/);
      if (url.protocol !== 'https:' || !isInstagram(url.hostname) || url.username || url.password || !match) return null;
      url.search = ''; url.hash = '';
      return {url:url.href, code:match[1], origin:url.origin};
    } catch {return null;}
  }
  function direct(value) {
    try {
      const url = new URL(value);
      const host = url.hostname;
      if (url.protocol !== 'https:' || url.username || url.password || value.length > 16000) return '';
      if (!(host.endsWith('.cdninstagram.com') || host.endsWith('.fbcdn.net'))) return '';
      // DASH initialization files and byte-range URLs are not complete videos.
      if (!/\.mp4$/i.test(url.pathname) || /dash|\.m4[sv]$/i.test(url.pathname) ||
          [...url.searchParams.keys()].some(key => /^(?:bytestart|byteend|range)$/i.test(key))) return '';
      return url.href;
    } catch {return '';}
  }
  function numericId(code) {
    // Instagram shortcodes encode the media ID in base 64. Keep it as a
    // decimal string: current IDs exceed JavaScript's exact Number range.
    if (typeof code !== 'string' || !/^[A-Za-z0-9_-]{1,28}$/.test(code)) return '';
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
    let value = 0n;
    for (const char of code) value = value * 64n + BigInt(alphabet.indexOf(char));
    return value.toString();
  }
  function mediaId(value) {
    if (typeof value === 'number' && Number.isSafeInteger(value) && value >= 0) return String(value);
    return typeof value === 'string' ? value.match(/^(\d{1,52})(?:_\d{1,26})?$/)?.[1] || '' : '';
  }
  function identity(node, code, id) {
    const codes = [node.code,node.shortcode].filter(value => typeof value === 'string' && value);
    const ids = [mediaId(node.pk),mediaId(node.id)].filter(Boolean);
    if (codes.some(value => value !== code) || (id && ids.some(value => value !== id))) return -1;
    return codes.includes(code) || (id && ids.includes(id)) ? 1 : 0;
  }
  function newStats() {return {scripts:0,nodes:0,matched:0,dash:false,limited:false};}
  function fromData(data, code, stats = newStats()) {
    const stack = [data], seen = new Set(), id = numericId(code);
    const candidates = [];
    const collect = node => {
      if (!node || typeof node !== 'object' || identity(node,code,id) < 0) return;
      // Do not choose one video from a carousel, a poster, or a gated response.
      if (node.is_video === false || ['1','8'].includes(String(node.media_type)) || node.carousel_media || node.edge_sidecar_to_children) return;
      if (typeof node.video_dash_manifest === 'string' && node.video_dash_manifest.includes('<MPD')) stats.dash = true;
      const url = direct(node.video_url);
      if (url) candidates.push({url,pixels:0});
      if (Array.isArray(node.video_versions)) for (const version of node.video_versions.slice(0,50)) {
        const value = direct(version?.url);
        if (value) candidates.push({url:value,pixels:Math.max(0,Math.min(1e8,Number(version.width)*Number(version.height)||0))});
      }
    };
    while (stack.length && stats.nodes < 100000) {
      const node = stack.pop();
      if (!node || typeof node !== 'object' || seen.has(node)) continue;
      seen.add(node); stats.nodes++;
      if (identity(node,code,id) === 1) {
        stats.matched++;
        collect(node);
        // Current Polaris payloads may put the selected media's data under
        // this exact wrapper. Null means gated/unavailable: never work around it.
        if (!node.carousel_media && !node.edge_sidecar_to_children && String(node.media_type) !== '8') collect(node.if_not_gated_logged_out);
      }
      for (const value of Object.values(node)) if (value && typeof value === 'object') stack.push(value);
    }
    if (stack.length) stats.limited = true;
    candidates.sort((a,b) => b.pixels-a.pixels);
    return candidates[0]?.url || '';
  }
  function scanDocument(doc, target) {
    const stats = newStats(), id = numericId(target.code);
    let remaining = MAX_HTML;
    const scripts = doc.querySelectorAll('script[type="application/json"],script[type="application/ld+json"],script[data-sjs]');
    if (scripts.length > 200) stats.limited = true;
    for (const script of Array.from(scripts).slice(0,200)) {
      const text = script.textContent || '';
      remaining -= text.length;
      if (remaining < 0 || stats.nodes >= 100000) {stats.limited = true;break;}
      if (!text.includes(target.code) && !(id && text.includes(id))) continue;
      // data-sjs is JSON, not executable script. Never eval assignment wrappers.
      try {const data = JSON.parse(text);stats.scripts++;const found = fromData(data,target.code,stats);if (found) return {...stats,url:found};} catch {}
    }
    // Only trust page-level video metadata when it identifies this exact Reel.
    const canonical = reel(doc.querySelector('link[rel="canonical"]')?.getAttribute('href'));
    const ogPage = reel(doc.querySelector('meta[property="og:url"]')?.getAttribute('content'));
    if ((canonical || ogPage) && [canonical,ogPage].filter(Boolean).every(value => value.code === target.code)) {
      for (const meta of doc.querySelectorAll('meta[property="og:video"],meta[property="og:video:url"],meta[property="og:video:secure_url"]')) {
        const found = direct(meta.getAttribute('content')); if (found) return {...stats,url:found};
      }
    }
    return {...stats,url:''};
  }
  function fromDocument(doc, target) {return scanDocument(doc,target).url;}
  function embedUrl(target) {return `${target.origin}/reel/${target.code}/embed/`;}
  function isEmbedUrl(value, target) {
    try {
      const url = new URL(value);
      return url.origin === target.origin && !url.username && !url.password &&
        new RegExp(`^/(?:reels?|p)/${target.code}/embed/(?:captioned/)?$`).test(url.pathname);
    } catch {return false;}
  }
  function embedContext(text, target) {
    // Public embed pages put a JSON string in ServerJS's contextJSON property.
    // Parse ONLY that string literal as JSON; never execute the script wrapper.
    if (typeof text !== 'string' || text.length > MAX_HTML) return '';
    const pattern = /"contextJSON"\s*:\s*("(?:[^"\\]|\\.)*")/g;
    let match, count = 0;
    while ((match = pattern.exec(text)) && count++ < 32) {
      try {
        if (!/^\s*[,}]/.test(text.slice(pattern.lastIndex,pattern.lastIndex+128))) continue;
        const data = JSON.parse(JSON.parse(match[1]));
        const context = data?.context, node = data?.gql_data?.shortcode_media;
        if (context?.shortcode !== target.code || context.type !== 'GraphVideo' || context.copyright_blocked === true) continue;
        if (!node || node.is_video !== true || node.copyright_blocked === true || node.is_embeds_disabled === true ||
            node.carousel_media || node.edge_sidecar_to_children || identity(node,target.code,numericId(target.code)) !== 1) continue;
        const url = fromData(node,target.code);
        if (url) return url;
      } catch {} // Non-JSON JS, unrelated posts, and gated data are not usable.
    }
    return '';
  }
  function fromEmbedDocument(doc, target) {
    let remaining = MAX_HTML;
    for (const script of Array.from(doc.querySelectorAll('script:not([src])')).slice(0,200)) {
      const text = script.textContent || '';
      remaining -= text.length; if (remaining < 0) break;
      const url = embedContext(text,target); if (url) return url;
    }
    return '';
  }
  function metadataFailure(...reports) {
    const limited = reports.some(value => value.limited), matched = reports.some(value => value.matched), dash = reports.some(value => value.dash);
    if (limited) return new Error('页面视频数据超过解析上限（IG12-LIMIT），本次未收藏。');
    if (dash) return new Error('已找到这条视频的分段清单，但暂未取得可保存的完整视频（IG12-DASH）。本次未收藏。');
    if (matched) return new Error('已匹配这条视频，但没有找到可保存的完整视频地址（IG12-URL）。本次未收藏。');
    return new Error('未匹配到这条视频的页面数据（IG12-META），尚不能确定原因。本次未收藏，未保存为网址或封面。');
  }
  function identify(video, pageUrl = location.href) {
    let page; try {page = new URL(pageUrl);} catch {return '';}
    if (page.protocol !== 'https:' || !isInstagram(page.hostname)) return '';
    // Use the enclosing card, not the whole feed. React layouts often contain
    // more than six wrappers; the permalink can also be the enclosing <a>.
    for (let node = video.parentElement, level = 0; node && level < 24; node = node.parentElement, level++) {
      if (node.matches('body,html')) break;
      if (node.querySelectorAll('video').length !== 1) break;
      const targets = new Map();
      const links = [...(node.matches('a[href]') ? [node] : []),...node.querySelectorAll('a[href]')];
      if (links.length > 80) return '';
      for (const link of links) {
        let value; try {value = reel(new URL(link.getAttribute('href'),pageUrl).href);} catch {}
        if (value) targets.set(value.code,value.url);
      }
      if (targets.size === 1) return targets.values().next().value;
      if (targets.size > 1) return '';
      if (node.matches('article,[role="dialog"],dialog')) break;
    }
    const current = reel(pageUrl);
    if (!current) return '';
    const doc = video.ownerDocument, view = doc.defaultView;
    const videos = doc.querySelectorAll('video');
    if (videos.length === 1 && videos[0] === video) return current.url;
    if (!view || videos.length > 64) return '';
    // Independent Reel pages may preload other videos below the viewport or
    // hide them for buffering. Do not mistake those for competing visible
    // targets; never use this fallback on the generic /reels feed or when two
    // videos are visible. An explicit conflicting card link already returned.
    const visible = node => {
      const rect = node.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0 || rect.right <= 0 || rect.bottom <= 0 || rect.left >= view.innerWidth || rect.top >= view.innerHeight) return false;
      for (let parent = node; parent; parent = parent.parentElement) {
        const style = view.getComputedStyle(parent);
        if (parent.hidden || parent.getAttribute('aria-hidden') === 'true' || parent.hasAttribute('inert') || style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || Number(style.opacity) === 0) return false;
      }
      return true;
    };
    const targets = Array.from(videos).filter(visible);
    return targets.length === 1 && targets[0] === video ? current.url : '';
  }
  async function readBytes(response, max) {
    if (Number(response.headers.get('content-length')) > max) throw new Error('资源太大，未继续读取');
    const reader = response.body.getReader(), parts = []; let length = 0;
    try {
      while (true) {
        const {value,done} = await reader.read(); if (done) break;
        length += value.length;
        if (length > max) throw new Error('资源太大，未继续读取');
        parts.push(value);
      }
    } finally {try {await reader.cancel();} catch {}}
    const result = new Uint8Array(length); let offset = 0;
    for (const part of parts) {result.set(part,offset); offset += part.length;}
    return result;
  }
  async function resolve(item, doc = document, pageUrl = location.href, fetcher = fetch) {
    if (item?.kind !== 'video' || !String(item.url).startsWith('blob:')) return item;
    const page = new URL(pageUrl), target = reel(item.mediaPage);
    if (!isInstagram(page.hostname)) throw new Error('此视频只有临时播放地址，尚未获取完整视频文件。本次未保存为网址或封面。');
    if (!target || target.origin !== page.origin) {
      throw new Error('未能确定所选视频的独立链接（IG13-TARGET）。本次未收藏，不会保存成网址或封面。');
    }
    let blob; try {blob = new URL(item.url);} catch {}
    if (!blob || blob.origin !== page.origin) throw new Error('视频地址与当前页面不匹配，请刷新视频页面后重试。');
    const initial = scanDocument(doc,target);
    if (initial.url) return {...item,url:initial.url,source:target.url};
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(),18000);
    let embedTried = false;
    const tryEmbed = async () => {
      if (embedTried) return '';
      embedTried = true;
      try {
        const response = await fetcher(embedUrl(target),{credentials:'omit',mode:'same-origin',signal:controller.signal});
        if (!response.ok || !isEmbedUrl(response.url,target)) return '';
        const bytes = await readBytes(response,MAX_HTML);
        const inert = doc.createElement('template'); inert.innerHTML = new TextDecoder().decode(bytes);
        return fromEmbedDocument(inert.content,target);
      } catch (error) {if (controller.signal.aborted) throw error;return '';}
    };
    try {
      // Ordinary Blob videos are readable in their originating page; MSE blobs
      // are not. Do not turn a failed MSE fetch into a screenshot or silent file.
      try {
        const response = await fetcher(item.url,{signal:controller.signal});
        const type = response.headers.get('content-type')?.split(';')[0];
        if (response.ok && ['video/mp4','video/webm'].includes(type)) {
          const bytes = await readBytes(response,MAX_VIDEO);
          let binary = '';
          for (let i=0;i<bytes.length;i+=16384) binary += String.fromCharCode(...bytes.subarray(i,i+16384));
          return {...item,url:`data:${type};base64,${btoa(binary)}`,source:target.url};
        }
      } catch (error) {if (controller.signal.aborted) throw error;}
      // A matching but incomplete live-page record is the IG12-URL case.
      // Prefer its small public embed before requesting the full page again.
      if (initial.matched) {
        const url = await tryEmbed();
        if (url) return {...item,url,source:target.url};
      }
      // One same-origin page request uses the browser's existing session. Never
      // extract login credentials or work around a login/checkpoint response.
      const response = await fetcher(target.url,{credentials:'same-origin',mode:'same-origin',signal:controller.signal});
      if (!response.ok) throw new Error(`Instagram 未提供视频数据（HTTP ${response.status}），请确认该视频能在当前页面播放。`);
      const finalPage = reel(response.url);
      if (!finalPage || finalPage.code !== target.code || finalPage.origin !== target.origin) throw new Error('Instagram 要求登录或验证，请先在网页完成，再重新收藏。');
      const bytes = await readBytes(response,MAX_HTML);
      const html = new TextDecoder().decode(bytes);
      // Template contents remain inert: no scripts, images or frames execute/load.
      const template = doc.createElement('template'); template.innerHTML = html;
      const fetched = scanDocument(template.content,target);
      if (fetched.url) return {...item,url:fetched.url,source:target.url};
      // The public, same-origin embed can include the complete progressive MP4
      // even when the normal page includes only partial media metadata. No
      // account credentials are sent, no private API or downloader is involved.
      const url = await tryEmbed();
      if (url) return {...item,url,source:target.url};
      throw metadataFailure(initial,fetched);
    } catch (error) {
      if (controller.signal.aborted) throw new Error('解析 Instagram 视频超时，请确认视频可播放后重试。');
      throw error;
    } finally {clearTimeout(timer);}
  }
  globalThis.MoriMediaSource = {reel,direct,numericId,fromData,fromDocument,scanDocument,embedUrl,isEmbedUrl,embedContext,fromEmbedDocument,identify,resolve};
})();
