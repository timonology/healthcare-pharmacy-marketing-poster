/**
 * The shortest password the API accepts for a new password (sign-up, reset,
 * invitation). Checked in the browser as a courtesy so nobody is told "too
 * short" only after submitting; the API's PasswordPolicy is the real rule and
 * also refuses common passwords. Keep the two in step.
 */
export const MIN_PASSWORD_LENGTH = 12;
