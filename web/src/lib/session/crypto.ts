// Oturum çerezi şifreleme (ADR-0006). Yalnızca Node.js yerleşik `node:crypto`: AES-256-GCM (gizlilik + bütünlük).
// Anahtar, SESSION_SECRET'tan HKDF-SHA256 ile türetilir. Bu modül Next.js'e bağımlı değildir (birim testi için).
import { createCipheriv, createDecipheriv, hkdfSync, randomBytes } from "node:crypto";

const VERSION = "v1";
const IV_LENGTH = 12;
const TAG_LENGTH = 16;
const MIN_SECRET_LENGTH = 32;

function deriveKey(secret: string): Buffer {
  if (secret.length < MIN_SECRET_LENGTH) {
    throw new Error(`SESSION_SECRET en az ${MIN_SECRET_LENGTH} karakter olmalıdır.`);
  }
  return Buffer.from(hkdfSync("sha256", secret, "sokagimdakisorun-panel", "session-cookie-v1", 32));
}

/** Değeri şifreler; çıktı çerezde taşınabilir (base64url, noktayla ayrılmış). */
export function seal(value: unknown, secret: string): string {
  const key = deriveKey(secret);
  const iv = randomBytes(IV_LENGTH);
  const cipher = createCipheriv("aes-256-gcm", key, iv, { authTagLength: TAG_LENGTH });
  const plaintext = Buffer.from(JSON.stringify(value), "utf8");
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const tag = cipher.getAuthTag();
  return [VERSION, iv.toString("base64url"), ciphertext.toString("base64url"), tag.toString("base64url")].join(".");
}

/** Şifreyi çözer; bozuk, kurcalanmış veya farklı anahtarla şifrelenmiş değerde `null` döner (istisna fırlatmaz). */
export function unseal<T>(sealed: string | undefined | null, secret: string): T | null {
  if (!sealed) return null;
  const parts = sealed.split(".");
  if (parts.length !== 4 || parts[0] !== VERSION) return null;
  try {
    const key = deriveKey(secret);
    const iv = Buffer.from(parts[1]!, "base64url");
    const ciphertext = Buffer.from(parts[2]!, "base64url");
    const tag = Buffer.from(parts[3]!, "base64url");
    if (iv.length !== IV_LENGTH || tag.length !== TAG_LENGTH) return null;
    const decipher = createDecipheriv("aes-256-gcm", key, iv, { authTagLength: TAG_LENGTH });
    decipher.setAuthTag(tag);
    const plaintext = Buffer.concat([decipher.update(ciphertext), decipher.final()]);
    return JSON.parse(plaintext.toString("utf8")) as T;
  } catch {
    return null;
  }
}
