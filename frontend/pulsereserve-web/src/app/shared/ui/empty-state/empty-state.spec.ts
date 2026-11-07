import { TestBed } from '@angular/core/testing';
import { EmptyState } from './empty-state';

describe('EmptyState', () => {
  it('renders heading and message in a labelled region', async () => {
    const fixture = TestBed.createComponent(EmptyState);
    fixture.componentRef.setInput('heading', 'Nothing here');
    fixture.componentRef.setInput('message', 'Try something else.');
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h2')?.textContent).toBe('Nothing here');
    expect(el.querySelector('p')?.textContent).toBe('Try something else.');
    expect(el.querySelector('section')?.getAttribute('aria-label')).toBe('Nothing here');
  });
});
