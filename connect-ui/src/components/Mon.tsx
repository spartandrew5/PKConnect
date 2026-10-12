import { useEffect, useState } from 'react';
import { api } from '../api/client';
import type { PartyMon, SpeciesSprite } from '../api/types';

// Asked for once; every Pokémon shown afterwards reads from it.
let sprites: Promise<(SpeciesSprite | null)[]> | undefined;

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

/** A species' picture, cut from its sprite sheet and scaled to a square box. */
export function MonSprite({ species, size, fallback }: { species: number; size: number; fallback: string }) {
  const sprite = useSpeciesSprite(species);
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
export function Mon({ mon }: { mon: PartyMon }) {
  return (
    <div className="pmon" title={mon.nickname}>
      {/* At the sheet's own size: pixel art scaled by anything but a whole number loses rows of pixels. */}
      <MonSprite species={mon.species} size={96} fallback={mon.nickname} />
      <b>{mon.nickname}</b>
      <small>{mon.isEgg ? 'Egg' : `Lv. ${mon.level}`}</small>
    </div>
  );
}