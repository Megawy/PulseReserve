/** Client-side checks are a courtesy only: the server validates everything again and its answer is shown. */
export function isPlausibleEmail(value: string): boolean {
  const v = value.trim();
  return v.length > 0 && v.length <= 254 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v);
}

export const MIN_PASSWORD_LENGTH = 12;

/** Strength label for the register form's hint. Length is the only rule the server enforces; this is guidance. */
export function passwordHint(
  password: string,
): { label: string; tone: 'danger' | 'warning' | 'success' } | null {
  if (!password) {
    return null;
  }
  if (password.length < MIN_PASSWORD_LENGTH) {
    return {
      label: `${MIN_PASSWORD_LENGTH - password.length} more characters needed`,
      tone: 'danger',
    };
  }
  const classes = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter((r) => r.test(password)).length;
  return classes >= 3 || password.length >= 16
    ? { label: 'Strong password', tone: 'success' }
    : {
        label: 'Long enough. Mixing letters, numbers and symbols makes it stronger',
        tone: 'warning',
      };
}
