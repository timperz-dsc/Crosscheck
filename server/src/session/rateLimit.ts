/** At most `max` hits per key within `windowMs` (sliding window). */
export class RateLimiter {
  private readonly hits = new Map<string, number[]>();

  constructor(private readonly max: number, private readonly windowMs: number) {}

  /** Records a hit. Returns 0 if allowed, otherwise the ms until the next hit would be allowed. */
  hit(key: string, now = Date.now()): number {
    const recent = (this.hits.get(key) ?? []).filter((t) => now - t < this.windowMs);
    if (recent.length >= this.max) {
      this.hits.set(key, recent);
      return recent[0]! + this.windowMs - now;
    }
    recent.push(now);
    this.hits.set(key, recent);
    return 0;
  }

  /** Forgets keys without recent hits (call now and then so the map cannot grow forever). */
  prune(now = Date.now()): void {
    for (const [key, times] of this.hits) {
      if (times.every((t) => now - t >= this.windowMs)) this.hits.delete(key);
    }
  }
}
