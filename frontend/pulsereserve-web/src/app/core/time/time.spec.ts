import { computeCountdown, formatRemaining } from './countdown';
import { ServerClock } from './server-clock';

describe('computeCountdown', () => {
  const now = Date.parse('2026-10-21T10:00:00Z');

  it('reports remaining whole seconds, rounding up', () => {
    expect(computeCountdown('2026-10-21T10:10:00Z', now)).toEqual({
      kind: 'running',
      remainingSeconds: 600,
    });
    expect(computeCountdown('2026-10-21T10:00:00.400Z', now)).toEqual({
      kind: 'running',
      remainingSeconds: 1,
    });
  });

  it('is expired at and after the expiry instant', () => {
    expect(computeCountdown('2026-10-21T10:00:00Z', now)).toEqual({ kind: 'expired' });
    expect(computeCountdown('2026-10-21T09:00:00Z', now)).toEqual({ kind: 'expired' });
  });

  it('flags unparsable timestamps instead of guessing', () => {
    expect(computeCountdown('', now)).toEqual({ kind: 'invalid' });
    expect(computeCountdown('soon', now)).toEqual({ kind: 'invalid' });
  });

  it('formats m:ss', () => {
    expect(formatRemaining(600)).toBe('10:00');
    expect(formatRemaining(65)).toBe('1:05');
    expect(formatRemaining(-3)).toBe('0:00');
  });
});

describe('ServerClock', () => {
  it('derives an offset from the Date header (browser clock behind the server)', () => {
    const clock = new ServerClock();
    const received = Date.parse('2026-10-21T10:00:00Z');
    clock.observe('Wed, 21 Oct 2026 10:05:00 GMT', received);
    expect(clock.offset).toBe(5 * 60 * 1000);
  });

  it('ignores missing, invalid or absurd headers', () => {
    const clock = new ServerClock();
    clock.observe(null);
    clock.observe('garbage');
    clock.observe('Wed, 21 Oct 2020 10:05:00 GMT', Date.parse('2026-10-21T10:00:00Z'));
    expect(clock.offset).toBe(0);
  });

  it('now() applies the offset', () => {
    const clock = new ServerClock();
    clock.observe('Wed, 21 Oct 2026 10:05:00 GMT', Date.parse('2026-10-21T10:00:00Z'));
    expect(Math.abs(clock.now() - (Date.now() + 300_000))).toBeLessThan(50);
  });
});
