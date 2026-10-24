import type { CSSProperties } from 'react';
import type { RegionId } from '../api/types';

/** Tints whatever it is put on with a region's colour. */
export function regionStyle(region: RegionId | undefined): CSSProperties | undefined {
  return region ? ({ '--region': `var(--${region})` } as CSSProperties) : undefined;
}

/**
 * The colours of the game a trainer is in (an expansion build wears the game it is built as), with
 * the region's colour standing in before there is a game.
 */
export function gameStyle(gameId: string | undefined, region: RegionId | undefined): CSSProperties | undefined {
  const game = gameId?.replace(/expansion$/, '');
  if (!game) return regionStyle(region);
  const fallback = region ? `var(--${region})` : 'var(--region)';
  return { '--region': `var(--g-${game}, ${fallback})`, '--on-region': `var(--g-${game}-on, #fff)` } as CSSProperties;
}

/** How a trainer gets to each region: where the ship comes in, and its name. */
export const PORTS: Record<RegionId, { port: string; vessel: string }> = {
  kanto: { port: 'Vermilion City', vessel: 'S.S. Anne' },
  johto: { port: 'Olivine City', vessel: 'S.S. Aqua' },
  hoenn: { port: 'Slateport City', vessel: 'S.S. Tidal' },
  sinnoh: { port: 'Canalave City', vessel: 'Canalave ferry' },
  unova: { port: 'Castelia City', vessel: 'Royal Unova' },
};

/** "Oct 9" - and the year, once it isn't this one. */
export function shortDate(iso: string): string {
  const date = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : `${iso}Z`);
  const sameYear = date.getFullYear() === new Date().getFullYear();
  return date.toLocaleDateString('en', sameYear ? { month: 'short', day: 'numeric' } : { month: 'short', day: 'numeric', year: 'numeric' });
}

/** "a", "a and b", "a, b, and c". */
export function listOf(items: string[]): string {
  if (items.length < 2) return items.join('');
  return `${items.slice(0, -1).join(', ')}${items.length > 2 ? ',' : ''} and ${items[items.length - 1]}`;
}