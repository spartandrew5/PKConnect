import type { Appearance, Catalog, Character, SpriteKind, SpriteSet } from '../api/types';

export const GENERATIONS = [1, 2, 3, 4, 5];

// The two Game Boy generations share their art (it is re-fitted at launch), so each is offered
// the other's sets after its own. The backend resolves sets the same way.
const SOURCES: Record<number, number[]> = { 1: [1, 2], 2: [2, 1] };

/** A character's sets of one kind that a generation can use, its own generation's first. */
export function setsFor(character: Character | undefined, generation: number, kind: SpriteKind): SpriteSet[] {
  if (!character) return [];
  return (SOURCES[generation] ?? [generation]).flatMap((gen) => character.sets.filter((set) => set.generation === gen && set.kind === kind));
}

/** The generations a character has any sprites for. */
export function generationsOf(character: Character | undefined): number[] {
  return GENERATIONS.filter((generation) => (['front', 'overworld'] as const).some((kind) => setsFor(character, generation, kind).length > 0));
}

/** The sets used for a generation: each saved choice if it is still offered, else the first. */
export function chosenSets(
  character: Character | undefined,
  look: Appearance | undefined,
  generation: number,
): Partial<Record<'front' | 'overworld', SpriteSet>> {
  const choice = look?.sprites?.[String(generation)];
  const pick = (kind: 'front' | 'overworld', id?: string) => {
    const options = setsFor(character, generation, kind);
    return options.find((set) => set.id === id) ?? options[0];
  };
  return { front: pick('front', choice?.front), overworld: pick('overworld', choice?.overworld) };
}

/**
 * The battle sprite a character is shown by. With a generation, the one picked for it; when the
 * character has none there, the nearest generation's.
 */
export function portraitOf(catalog: Catalog, characterId: string | undefined, look?: Appearance, generation?: number): SpriteSet | undefined {
  const character = catalog.characters.find((c) => c.id === characterId);
  if (!character) return undefined;
  const available = generationsOf(character).filter((gen) => setsFor(character, gen, 'front').length > 0);
  if (available.length === 0) return undefined;
  const nearest =
    generation === undefined
      ? available[available.length - 1]!
      : [...available].sort((a, b) => Math.abs(a - generation) - Math.abs(b - generation) || a - b)[0]!;
  return chosenSets(character, look?.character === character.id ? look : undefined, nearest).front;
}