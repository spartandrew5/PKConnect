import { useContext, useEffect, useState } from 'react';
import { api } from '../api/client';
import type { PartyMon, SpeciesSprite } from '../api/types';
import { SpriteGame, gameSpriteSources, prepareSprite } from './gameSprites';

// Asked for once; every Pokémon shown afterwards reads from it.
let sprites: Promise<(SpeciesSprite | null)[]> | undefined;
let speciesNames: Promise<string[]> | undefined;

function useSpeciesSprite(species: number): SpeciesSprite | undefined {
  const [sprite, setSprite] = useState<SpeciesSprite>();
  useEffect(() => {
    let current = true;
    sprites ??= api.speciesSprites().catch(() => []);
    sprites.then((all) => current && setSprite(all[species] ?? undefined));
    return () => {
      current = false;
    };
  }, [species]);
  return sprite;
}

/**
 * The species as the game on screen draws it (see gameSprites.ts), once it has loaded.
 * Undefined until then, and for good when the game's art doesn't have the species.
 */
function useGameSprite(species: number, shiny: boolean): string | undefined {
  const game = useContext(SpriteGame);
  const [shown, setShown] = useState<{ key: string; url: string }>();
  const key = `${game?.id}/${species}/${shiny}`;

  useEffect(() => {
    let current = true;
    speciesNames ??= api.names().then((names) => names.species, () => []);
    speciesNames.then(async (names) => {
      for (const source of gameSpriteSources(game, species, names[species], shiny)) {
        try {
          const url = await prepareSprite(source);
          // Only shown once it has really arrived, so a missing file never leaves a broken picture.
          await new Promise<void>((resolve, reject) => {
            const probe = new Image();
            probe.onload = () => resolve();
            probe.onerror = () => reject(new Error('missing'));
            probe.src = url;
          });
          if (current) setShown({ key, url });
          return;
        } catch {
          // On to the next source, and to the sprite sheet after the last.
        }
      }
    });
    return () => {
      current = false;
    };
  }, [game, species, shiny, key]);

  return shown?.key === key ? shown.url : undefined;
}

// The games' small menu icons (Generation 7's, which cover every species there is to meet here).
const ICONS = 'https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/versions/generation-vii/icons';

/**
 * Pixel art only stays crisp when it is drawn bigger by a whole number. Drawn smaller, sharp
 * scaling drops whole rows and columns of pixels, which is what makes a small sprite look broken;
 * smoothing keeps every pixel's share of the picture instead.
 */
function fitRendering(image: HTMLImageElement) {
  image.style.imageRendering = image.naturalWidth > image.clientWidth ? 'auto' : 'pixelated';
}

/**
 * A species' picture scaled to a square box: the current game's own sprite, or the sheet's until
 * (or unless) that loads. With `icon`, the small menu icon the games draw for sizes like this instead.
 */
export function MonSprite({
  species,
  size,
  fallback,
  shiny = false,
  icon = false,
}: {
  species: number;
  size: number;
  fallback: string;
  shiny?: boolean;
  icon?: boolean;
}) {
  const sprite = useSpeciesSprite(species);
  const gameSprite = useGameSprite(species, shiny);
  const [iconMissing, setIconMissing] = useState(false);

  if (icon && species > 0 && !iconMissing) {
    return (
      <span className="monsprite icon" style={{ width: size, height: size }}>
        <img src={`${ICONS}/${species}.png`} alt="" draggable={false} onError={() => setIconMissing(true)} />
      </span>
    );
  }
  if (gameSprite) {
    return (
      <span className="monsprite game" style={{ width: size, height: size }}>
        <img src={gameSprite} alt="" draggable={false} onLoad={(event) => fitRendering(event.currentTarget)} />
      </span>
    );
  }
  if (!sprite) {
    return (
      <span className="dot" style={{ width: size * 0.6, height: size * 0.6 }}>
        {fallback.slice(0, 2)}
      </span>
    );
  }
  // Drawn at the sheet's own size and scaled as a whole, so nothing has to know how big the sheet is.
  const scale = size / sprite.width;
  return (
    <span className="monsprite" style={{ width: size, height: size }}>
      <span
        style={{
          width: sprite.width,
          height: sprite.height,
          backgroundImage: `url(/imgs/sheets/${sprite.sheet})`,
          backgroundPosition: `-${sprite.x}px -${sprite.y}px`,
          transform: `scale(${scale})`,
          // Crisp only when each pixel becomes a whole number of pixels; otherwise let the browser smooth it.
          imageRendering: Number.isInteger(scale) ? 'pixelated' : 'auto',
        }}
      />
    </span>
  );
}
/** A party member: its picture, with its name and level underneath. */
export function Mon({ mon, onOpen }: { mon: PartyMon; onOpen?: () => void }) {
  const Tag = onOpen ? 'button' : 'div';
  return (
    <Tag className="pmon" title={mon.nickname} {...(onOpen ? { type: 'button' as const, onClick: onOpen } : {})}>
      {/* At the sheet's own size: pixel art scaled by anything but a whole number loses rows of pixels. */}
      <MonSprite species={mon.isEgg ? 0 : mon.species} size={96} fallback={mon.nickname} shiny={mon.isShiny} />
      <b>{mon.nickname}</b>
      <small>{mon.isEgg ? 'Egg' : `Lv. ${mon.level}`}</small>
    </Tag>
  );
}
