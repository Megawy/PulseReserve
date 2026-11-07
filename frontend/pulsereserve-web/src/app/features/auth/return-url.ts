/**
 * Only same-app absolute paths may be used as a post-login destination. Anything else (another origin,
 * protocol-relative `//host`, backslash tricks, `javascript:`) falls back to the home page, so the login page
 * cannot be abused as an open redirect.
 */
export function safeReturnUrl(candidate: string | null | undefined): string {
  if (
    typeof candidate === 'string' &&
    candidate.startsWith('/') &&
    !candidate.startsWith('//') &&
    !candidate.includes('\\') &&
    !/[\u0000-\u001f]/.test(candidate)
  ) {
    return candidate;
  }
  return '/';
}
