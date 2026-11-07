import { TestBed } from '@angular/core/testing';
import { Redirector } from './redirector';

describe('Redirector', () => {
  let assign: ReturnType<typeof vi.fn>;
  let original: Location;

  beforeEach(() => {
    original = globalThis.location;
    assign = vi.fn();
    Object.defineProperty(globalThis, 'location', {
      configurable: true,
      value: { ...original, assign },
    });
  });

  afterEach(() => {
    Object.defineProperty(globalThis, 'location', { configurable: true, value: original });
  });

  it('follows an absolute https URL', () => {
    const redirector = TestBed.inject(Redirector);

    expect(redirector.toProvider('https://checkout.stripe.com/c/pay/cs_test_1')).toBe(true);
    expect(assign).toHaveBeenCalledWith('https://checkout.stripe.com/c/pay/cs_test_1');
  });

  it.each([
    ['http (not secure)', 'http://checkout.example.test/pay'],
    ['a relative path', '/account/orders'],
    ['a javascript URL', 'javascript:alert(1)'],
    ['a data URL', 'data:text/html,hello'],
    ['garbage', 'not a url'],
    ['empty', ''],
    ['null', null],
    ['undefined', undefined],
  ])('refuses %s', (_name, url) => {
    const redirector = TestBed.inject(Redirector);

    expect(redirector.toProvider(url)).toBe(false);
    expect(assign).not.toHaveBeenCalled();
  });
});
