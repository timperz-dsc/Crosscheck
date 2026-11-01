/**
 * Counts corrections per var in a sliding window. If a var keeps needing corrections
 * (e.g. Fenix keeps changing it back on one side), the server stops correcting it.
 */
export class CorrectionGuard {
  private readonly times = new Map<string, number[]>();

  constructor(
    private readonly max: number,
    private readonly windowMs: number,
    private readonly now: () => number = Date.now,
  ) {}

  /** Corrections sent for this var within the window. */
  count(name: string): number {
    const list = this.times.get(name);
    if (!list) return 0;
    const cutoff = this.now() - this.windowMs;
    while (list.length > 0 && list[0]! <= cutoff) list.shift();
    if (list.length === 0) this.times.delete(name);
    return list.length;
  }

  /** true if one more correction is allowed. */
  allows(name: string): boolean {
    return this.count(name) < this.max;
  }

  record(name: string): void {
    const list = this.times.get(name) ?? [];
    list.push(this.now());
    this.times.set(name, list);
  }

  reset(name: string): void {
    this.times.delete(name);
  }
}
