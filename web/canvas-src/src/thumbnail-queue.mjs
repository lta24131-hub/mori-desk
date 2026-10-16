import { scheduleTimer, cancelTimer } from './timers.mjs';

// A thumbnail is optional derived data. Never delay a save ACK or publish a
// preview of newer/older unsaved content under an acknowledged revision.
export class CanvasThumbnailQueue {
  constructor({ capture, isCurrent, publish, timer = scheduleTimer, clear = cancelTimer, delay = 1000 }) {
    Object.assign(this, { capture, isCurrent, publish, timer, clear, delay });
    this.pending = null;
    this.sequence = 0;
    this.disposed = false;
  }
  schedule(token) {
    if (this.disposed) return;
    if (this.pending !== null) this.clear(this.pending);
    const sequence = ++this.sequence;
    this.pending = this.timer(async () => {
      this.pending = null;
      if (this.disposed || sequence !== this.sequence || !this.isCurrent(token)) return;
      try {
        const dataUrl = await this.capture();
        if (!this.disposed && sequence === this.sequence && this.isCurrent(token)) this.publish({ revision: token.revision, dataUrl });
      } catch { /* Saving/editing remains usable when preview rendering fails. */ }
    }, this.delay);
  }
  dispose() {
    this.disposed = true; this.sequence++;
    if (this.pending !== null) this.clear(this.pending);
    this.pending = null;
  }
}
