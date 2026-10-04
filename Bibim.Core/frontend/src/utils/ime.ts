/**
 * Korean/Japanese/Chinese IME guard.
 *
 * While composing Hangul, the Enter that COMMITS the composition fires a
 * keydown with `isComposing === true` (Chromium also reports keyCode 229).
 * Treating that Enter as "submit" sends the message mid-composition and
 * swallows the last syllable — the single most common Korean-input bug.
 * Every Enter-to-submit handler must bail out when this returns true.
 */
export function isImeComposing(e: React.KeyboardEvent): boolean {
  const ne = e.nativeEvent as KeyboardEvent;
  return ne.isComposing === true || ne.keyCode === 229;
}
