import { unwrapOptionalWireId, unwrapWireId, WireFormatError } from './wire-id';

/**
 * PROVISIONAL FIXTURES. The `{ value: "<guid>" }` shape is inferred from the C# source
 * (record struct IDs, no custom converter). These tests prove only that this mapper implements
 * that assumption. They do NOT prove the shape the running API actually emits.
 */
describe('wire-id (PROVISIONAL, source-derived assumption)', () => {
  const guid = '3f2b8a14-6c1d-4e5a-9b7f-0a1b2c3d4e5f';

  it('unwraps the assumed { value } shape into a plain string', () => {
    expect(unwrapWireId({ value: guid }, 'x')).toBe(guid);
  });

  it.each([
    ['a bare string', guid],
    ['null', null],
    ['undefined', undefined],
    ['an array', [guid]],
    ['an object without value', { id: guid }],
    ['a non-string value', { value: 42 }],
    ['a non-GUID value', { value: 'not-a-guid' }],
  ])('rejects %s with a WireFormatError naming the field', (_label, raw) => {
    expect(() => unwrapWireId(raw, 'event.id')).toThrow(WireFormatError);
    expect(() => unwrapWireId(raw, 'event.id')).toThrow(/event\.id/);
  });

  it('maps null/undefined to null for optional IDs and still validates present ones', () => {
    expect(unwrapOptionalWireId(null, 'x')).toBeNull();
    expect(unwrapOptionalWireId(undefined, 'x')).toBeNull();
    expect(unwrapOptionalWireId({ value: guid }, 'x')).toBe(guid);
    expect(() => unwrapOptionalWireId('bad', 'x')).toThrow(WireFormatError);
  });
});
