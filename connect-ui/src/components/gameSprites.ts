import { createContext } from 'react';
import type { Game } from '../api/types';

/**
 * A Pokémon as the game on screen draws it.
 *
 * Species the game has itself come from the PokeAPI sprite collection's folder for that game.
 * Species a Connect build adds to an older game come from the project whose art that build uses:
 *  - Gen 1 and 2 games: Emerald Seaglass (Game Boy Color style, up to #386);
 *  - Gen 3 games: pokeemerald-expansion;
 *  - Gen 4 games: hg-engine.
 * Nothing is bundled: the pictures are fetched from those projects' repositories, and a species
 * none of them has (or a failed fetch) falls back to the app's own sprite sheets.
 */
export const SpriteGame = createContext<Game | undefined>(undefined);

const POKEAPI = 'https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/versions';
const SEAGLASS = 'https://raw.githubusercontent.com/zitaoyu/seaglass-sprites/refs/heads/main/sprites';
const EXPANSION = 'https://raw.githubusercontent.com/rh-hideout/pokeemerald-expansion/master/graphics/pokemon';
const HG_ENGINE = 'https://raw.githubusercontent.com/BluRosie/hg-engine/main/data/graphics/sprites';

/** The game's own folder in the PokeAPI collection; Gen 1 and 2 keep their cut-out sprites one level down. */
const FOLDERS: Record<string, string> = {
  red: 'generation-i/red-blue/transparent',
  blue: 'generation-i/red-blue/transparent',
  yellow: 'generation-i/yellow/transparent',
  gold: 'generation-ii/gold/transparent',
  silver: 'generation-ii/silver/transparent',
  crystal: 'generation-ii/crystal/transparent',
  ruby: 'generation-iii/ruby-sapphire',
  sapphire: 'generation-iii/ruby-sapphire',
  emerald: 'generation-iii/emerald',
  emeraldexpansion: 'generation-iii/emerald',
  firered: 'generation-iii/firered-leafgreen',
  leafgreen: 'generation-iii/firered-leafgreen',
  fireredexpansion: 'generation-iii/firered-leafgreen',
  leafgreenexpansion: 'generation-iii/firered-leafgreen',
  diamond: 'generation-iv/diamond-pearl',
  pearl: 'generation-iv/diamond-pearl',
  platinum: 'generation-iv/platinum',
  heartgold: 'generation-iv/heartgold-soulsilver',
  soulsilver: 'generation-iv/heartgold-soulsilver',
  black: 'generation-v/black-white',
  white: 'generation-v/black-white',
  black2: 'generation-v/black-white',
  white2: 'generation-v/black-white',
};

/** The last species each generation's games were made with. */
const NATIVE_DEX = [0, 151, 251, 386, 493, 649];
const SEAGLASS_DEX = 386;

/** A picture to try: where it is, and what has to be done to the file before it can be shown. */
export interface SpriteSource {
  url: string;
  /** The file holds two frames; keep the top or the left one. */
  frame?: 'top' | 'left';
  /** The file has an opaque backdrop (the colour of its first pixel) to make transparent. */
  backdrop?: boolean;
}

/** "Mr. Mime" -> "mr_mime", "Ho-Oh" -> "ho_oh", "Farfetch'd" -> "farfetchd": how both decomps name a species' folder. */
function folderOf(name: string): string {
  return name
    .replace('♀', '_f')
    .replace('♂', '_m')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/['’.:]/g, '')
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_|_$/g, '');
}

/** The pictures to try for a species in a game, best first. Empty when only the sprite sheets have it. */
export function gameSpriteSources(game: Game | undefined, species: number, name: string | undefined, shiny: boolean): SpriteSource[] {
  const folder = game && FOLDERS[game.id];
  if (!game || !folder || species <= 0) return [];

  const native = NATIVE_DEX[game.generation] ?? 0;
  if (species <= native) {
    // The collection has shiny sprites for Gen 2 on, but Gen 2's are half size, so those stay normal.
    const dir = shiny && game.generation >= 3 ? `${folder}/shiny` : folder;
    return [{ url: `${POKEAPI}/${dir}/${species}.png` }];
  }

  if (game.generation <= 2) {
    return species <= SEAGLASS_DEX ? [{ url: `${SEAGLASS}/${shiny ? 'front_shiny' : 'front'}/${species}.png` }] : [];
  }
  if (!name) return [];
  if (game.generation === 3) {
    return [{ url: `${EXPANSION}/${folderOf(name)}/anim_front.png`, frame: 'top', backdrop: true }];
  }
  if (game.generation === 4) {
    return [{ url: `${HG_ENGINE}/${folderOf(name)}/male/front.png`, frame: 'left', backdrop: true }];
  }
  return [];
}

const prepared = new Map<string, Promise<string>>();

/**
 * The address to show a source from. A file that is ready as it is keeps its own address; one
 * that needs a frame cut out or its backdrop removed is redrawn once and kept for the session.
 */
export function prepareSprite(source: SpriteSource): Promise<string> {
  if (!source.frame && !source.backdrop) return Promise.resolve(source.url);

  let ready = prepared.get(source.url);
  if (!ready) {
    ready = new Promise<string>((resolve, reject) => {
      const image = new Image();
      image.crossOrigin = 'anonymous';
      image.onerror = () => reject(new Error(`No sprite at ${source.url}`));
      image.onload = () => {
        try {
          const width = source.frame === 'left' ? image.width / 2 : image.width;
          const height = source.frame === 'top' ? image.height / 2 : image.height;
          const canvas = document.createElement('canvas');
          canvas.width = width;
          canvas.height = height;
          const context = canvas.getContext('2d')!;
          context.drawImage(image, 0, 0, width, height, 0, 0, width, height);

          if (source.backdrop) {
            const pixels = context.getImageData(0, 0, width, height);
            const data = pixels.data;
            const [r, g, b, a] = [data[0], data[1], data[2], data[3]];
            if (a !== 0) {
              for (let i = 0; i < data.length; i += 4) {
                if (data[i] === r && data[i + 1] === g && data[i + 2] === b) data[i + 3] = 0;
              }
              context.putImageData(pixels, 0, 0);
            }
          }
          resolve(canvas.toDataURL('image/png'));
        } catch (error) {
          reject(error);
        }
      };
      image.src = source.url;
    });
    prepared.set(source.url, ready);
  }
  return ready;
}
