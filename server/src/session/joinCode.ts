import { randomBytes, randomInt } from 'node:crypto';

/** No look-alikes: 0/O, 1/I/L are left out. */
export const JOIN_CODE_ALPHABET = '23456789ABCDEFGHJKMNPQRSTUVWXYZ';
export const JOIN_CODE_LENGTH = 6;

export function generateJoinCode(): string {
  let code = '';
  for (let i = 0; i < JOIN_CODE_LENGTH; i++) code += JOIN_CODE_ALPHABET[randomInt(JOIN_CODE_ALPHABET.length)];
  return code;
}

/** Join codes are case-insensitive. Returns null if the input cannot be a code. */
export function normalizeJoinCode(input: string): string | null {
  const code = input.trim().toUpperCase();
  if (code.length !== JOIN_CODE_LENGTH) return null;
  for (const ch of code) if (!JOIN_CODE_ALPHABET.includes(ch)) return null;
  return code;
}

const token = (bytes: number) => randomBytes(bytes).toString('base64url');
export const newSessionId = () => `s_${token(9)}`;
export const newClientId = () => `c_${token(9)}`;
export const newResumeToken = () => token(32);
