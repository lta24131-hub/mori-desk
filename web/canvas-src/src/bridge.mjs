import { scheduleTimer, cancelTimer } from './timers.mjs';

export const PROTOCOL = 'nook-canvas-v1';

export function parseMessage(value) {
  try {
    const message = typeof value === 'string' ? JSON.parse(value) : value;
    return message && !Array.isArray(message) && typeof message === 'object' && message.protocol === PROTOCOL && typeof message.type === 'string' ? message : null;
  } catch { return null; }
}

// The native host owns durability. Saves are serialized because revision is the
// host's current acknowledged revision, not a client-generated sequence number.
export class CanvasBridge {
  constructor({ send, capture, apply, commit, normalize, setTheme, setInteraction = (_enabled) => {}, setToolVisibility = (_visible) => {}, onLoaded = () => {}, onSaved = (_token) => {}, insert, status, timer = scheduleTimer, clear = cancelTimer, ackTimeoutMs = 10000 }) {
    Object.assign(this, { send, capture, apply, commit, normalize, setTheme, setInteraction, setToolVisibility, onLoaded, onSaved, insert, status, timer, clear, ackTimeoutMs });
    this.session = null;
    this.loaded = false;
    this.generation = 0;
    this.change = 0;
    this.savedChange = 0;
    this.revision = 0;
    this.pending = new Map();
    this.pendingTimer = null;
    this.ackTimer = null;
    this.loading = false;
    this.suppress = false;
    this.preparing = false;
    this.forcedSaves = [];
    this.queuedAutosave = false;
    this.dirtyReported = false;
    this.applyChain = Promise.resolve();
    this.saveDelayMs = 800;
    this.importRequests = new Map();
  }
  post(type, payload = {}, requestId = crypto.randomUUID()) {
    this.send({ protocol: PROTOCOL, type, requestId, ...(this.session ? { session: this.session } : {}), payload });
    return requestId;
  }
  ready() { this.post('ready'); }
  cancelTimer() { if (this.pendingTimer !== null) this.clear(this.pendingTimer); this.pendingTimer = null; }
  cancelAckTimer() { if (this.ackTimer !== null) this.clear(this.ackTimer); this.ackTimer = null; }
  changed() {
    if (!this.loaded || this.loading || this.suppress) return;
    this.change++;
    if (!this.dirtyReported) { this.dirtyReported = true; this.post('dirty'); }
    this.status('dirty');
    this.cancelTimer();
    this.pendingTimer = this.timer(() => { this.pendingTimer = null; void this.save(); }, this.saveDelayMs);
  }
  failQueued(message) {
    this.queuedAutosave = false;
    for (const job of this.forcedSaves.splice(0)) this.post('error', { message }, job.requestId);
  }
  async save(requestId = crypto.randomUUID(), forced = false) {
    if (!this.loaded || this.loading) {
      if (forced) this.post('error', { message: '画布尚未加载，不能关闭或切换。' }, requestId);
      return;
    }
    this.cancelTimer();
    if (this.pending.size || this.preparing) {
      if (forced && !this.forcedSaves.some(job => job.requestId === requestId) && !this.pending.has(requestId)) this.forcedSaves.push({ requestId, generation: this.generation });
      else if (!forced) this.queuedAutosave = true;
      return;
    }
    if (!forced && this.change === this.savedChange) return;
    const generation = this.generation;
    this.preparing = true;
    try {
      if (forced) await this.commit();
      if (generation !== this.generation || !this.loaded) return;
      const scene = this.capture();
      const expectedRevision = this.revision;
      this.pending.set(requestId, { change: this.change, expectedRevision, generation, scene });
      this.status('saving');
      this.ackTimer = this.timer(() => {
        if (generation !== this.generation || !this.pending.has(requestId)) return;
        this.pending.delete(requestId);
        this.ackTimer = null;
        const message = '保存尚未确认，内容仍在窗口中。请稍后重试。';
        this.status('error', message, 'save');
        this.post('error', { message }, requestId);
        this.failQueued(message);
      }, this.ackTimeoutMs);
      this.post('save', { scene, revision: expectedRevision }, requestId);
    } catch (error) {
      if (generation !== this.generation) return;
      this.pending.delete(requestId);
      this.cancelAckTimer();
      const message = error instanceof Error ? error.message : String(error);
      this.status('error', message, 'save');
      this.post('error', { message }, requestId);
      this.failQueued(message);
    } finally {
      if (generation === this.generation) { this.preparing = false; this.drain(); }
    }
  }
  drain() {
    if (!this.loaded || this.loading || this.pending.size || this.preparing) return;
    const job = this.forcedSaves.shift();
    if (job) { if (job.generation === this.generation) void this.save(job.requestId, true); else this.drain(); }
    else if (this.queuedAutosave) { this.queuedAutosave = false; void this.save(); }
  }
  async receive(value) {
    const message = parseMessage(value);
    if (!message) return false;
    const { type, session, requestId, payload = {} } = message;
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) return false;
    if (type === 'load') {
      if (typeof session !== 'string' || !session) return false;
      // A session is immutable: a repeated load may never overwrite edits.
      if (session === this.session) return false;
      this.cancelTimer(); this.cancelAckTimer();
      const generation = ++this.generation;
      this.session = session;
      this.pending.clear(); this.forcedSaves.length = 0;
      this.importRequests.clear();
      this.preparing = false; this.queuedAutosave = false; this.dirtyReported = false;
      this.loaded = false; this.loading = true;
      const configuredDelay = payload.canvasPreferences?.saveDelay;
      this.saveDelayMs = Number.isSafeInteger(configuredDelay) ? Math.max(500, Math.min(10000, configuredDelay)) : 800;
      this.status('loading');
      const apply = this.applyChain.catch(() => {}).then(async () => {
        if (generation === this.generation) await this.apply(payload);
      });
      this.applyChain = apply;
      try {
        await apply;
        if (generation !== this.generation) return false;
        this.revision = Number.isSafeInteger(payload.revision) && payload.revision >= 0 ? payload.revision : 0;
        this.change = this.savedChange = 0;
        this.loaded = true; this.loading = false;
        this.status('saved');
        this.onLoaded();
        this.post('loaded', { revision: this.revision }, requestId);
      } catch (error) {
        if (generation !== this.generation) return false;
        this.loading = false;
        this.status('error', error instanceof Error ? error.message : String(error));
        this.post('error', { message: '画布加载失败；未覆盖现有项目。' }, requestId);
      }
      return true;
    }
    if (session !== this.session) return false;
    if (type === 'interaction') {
      if (typeof payload.enabled !== 'boolean') return false;
      this.setInteraction(payload.enabled); return true;
    }
    if (!this.loaded) return false;
    if (type === 'toolVisibility') {
      if (typeof payload.visible !== 'boolean') return false;
      this.setToolVisibility(payload.visible); return true;
    }
    if (type === 'flush') {
      if (typeof requestId !== 'string' || !requestId) return false;
      await this.save(requestId, true); return true;
    }
    if (type === 'theme') { this.setTheme(payload.theme); return true; }
    if (type === 'addImages') {
      if (typeof requestId !== 'string' || !requestId) return false;
      // Retrying a host message may acknowledge the original operation, but may
      // never create a second copy. A new command may intentionally reuse assets.
      const previous = this.importRequests.get(requestId);
      if (previous) {
        const result = await previous;
        if (result && result.generation === this.generation) this.post('imagesAdded', { count: result.count }, requestId);
        return true;
      }
      const generation = this.generation;
      const job = Promise.resolve().then(async () => {
        try {
          if (generation !== this.generation) return;
          const result = await this.insert(payload);
          if (generation !== this.generation) return;
          const count = Number.isSafeInteger(result?.count) && result.count >= 0 ? result.count : (payload.images || payload.files || payload.items || []).length;
          this.changed();
          this.post('imagesAdded', { count }, requestId);
          return { count, generation };
        } catch (error) {
          if (generation !== this.generation) return;
          this.importRequests.delete(requestId);
          this.status('error', error instanceof Error ? error.message : String(error));
          this.post('error', { message: error instanceof Error ? error.message : String(error) }, requestId);
        }
      });
      this.importRequests.set(requestId, job);
      await job;
      return true;
    }
    const pending = this.pending.get(requestId);
    if (!pending || pending.generation !== this.generation) return false;
    if (type === 'saved') {
      if (payload.revision !== pending.expectedRevision + 1) return false;
      this.pending.delete(requestId); this.cancelAckTimer();
      this.revision = payload.revision;
      this.savedChange = Math.max(this.savedChange, pending.change);
      this.suppress = true;
      try { this.normalize(payload, pending.scene); }
      catch { this.status('error', '图片已保存，但预览未更新；请关闭后重新打开画布。'); }
      finally { this.suppress = false; }
      if (this.savedChange === this.change) this.dirtyReported = false;
      this.status(this.savedChange === this.change ? 'saved' : 'dirty');
      // Auxiliary previews are best effort and never own the durability ACK.
      try { this.onSaved({ revision: this.revision, generation: this.generation, change: pending.change }); } catch { /* Keep save success independent of previews. */ }
      this.drain();
      return true;
    }
    if (type === 'error') {
      this.pending.delete(requestId); this.cancelAckTimer();
      if (Number.isSafeInteger(payload.revision) && payload.revision >= this.revision) this.revision = payload.revision;
      const message = payload.message || '保存失败，画布仍保留在窗口中。请重试。';
      this.status('error', message, 'save'); this.failQueued(message);
      return true;
    }
    return false;
  }
  dispose() {
    this.cancelTimer(); this.cancelAckTimer(); this.loaded = false;
    this.pending.clear(); this.importRequests.clear(); this.forcedSaves.length = 0; this.generation++;
  }
}
