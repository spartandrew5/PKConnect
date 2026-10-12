import { useState } from 'react';
import { api } from '../api/client';
import type { Appearance as Look, Catalog, SpriteSet, Trainer } from '../api/types';
import { chosenSets, generationsOf, portraitOf, setsFor } from '../components/looks';
import { Sprite } from '../components/Sprite';

const NUMERALS: Record<number, string> = { 1: 'I', 2: 'II', 3: 'III', 4: 'IV', 5: 'V' };
const KINDS = [
  ['front', 'Front'],
  ['overworld', 'Overworld'],
] as const;

/**
 * How a trainer looks in the games: a character, then for each generation the character has
 * sprites for, which front and which overworld set to use. Each change is saved as it is made
 * and applies at the next launch.
 */
export function Appearance({
  trainer,
  catalog,
  onChanged,
  onDone,
}: {
  trainer: Trainer;
  catalog: Catalog;
  onChanged: () => void;
  onDone: () => void;
}) {
  const [look, setLook] = useState<Look>(trainer.appearance ?? {});
  const [problem, setProblem] = useState<string>();

  const save = async (next: Look) => {
    setLook(next);
    setProblem(undefined);
    try {
      await api.setAppearance(trainer.id, next);
      onChanged();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };

  const character = catalog.characters.find((c) => c.id === look.character);
  const generations = generationsOf(character);
  const playing = catalog.games.find((game) => game.id === trainer.currentGameId)?.generation;

  // Another character starts from their own default sets.
  const pickCharacter = (id: string | undefined) => save({ character: id, sprites: {} });
  const pickSet = (generation: number, kind: 'front' | 'overworld', set: SpriteSet) =>
    save({ ...look, sprites: { ...look.sprites, [generation]: { ...look.sprites?.[String(generation)], [kind]: set.id } } });

  return (
    <section className="panel">
      <div className="dexhead">
        <div>
          <p className="eyebrow">Appearance</p>
          <h2>How {trainer.trainerName} looks</h2>
        </div>
        <button type="button" className="btn go" onClick={onDone}>
          Done
        </button>
      </div>
      <p className="muted" style={{ fontSize: 14 }}>
        Pick a trainer, then choose their front and overworld sprites for each generation. Any combination works; the back sprite follows the
        front. The chosen sprites are patched into the game on launch.
      </p>
      {problem && <p className="err">{problem}</p>}

      <div className="group">
        <p className="eyebrow">Trainer</p>
        <div className="lookgrid">
          <button type="button" className={`tile${look.character ? '' : ' on'}`} aria-pressed={!look.character} onClick={() => pickCharacter(undefined)}>
            <span className="spritebox" style={{ width: 104, height: 104 }}>
              <span className="muted">Game's own</span>
            </span>
            <b>None</b>
          </button>
          {catalog.characters.map((c) => (
            <button
              key={c.id}
              type="button"
              className={`tile${look.character === c.id ? ' on' : ''}`}
              aria-pressed={look.character === c.id}
              title={c.name}
              onClick={() => pickCharacter(c.id)}
            >
              <Sprite set={portraitOf(catalog, c.id, look, playing)} size={104} />
              <b>{c.name}</b>
            </button>
          ))}
        </div>
      </div>

      {character ? (
        <div className="group">
          <p className="eyebrow">{character.name}'s sprites</p>
          {generations.map((generation) => {
            const chosen = chosenSets(character, look, generation);
            const games = catalog.games.filter((game) => game.generation === generation && !game.id.endsWith('expansion')).map((game) => game.name);
            return (
              <div key={generation} className="gencard">
                <p className="eyebrow">
                  Generation {NUMERALS[generation] ?? generation}
                  {games.length > 0 && <span className="games"> · {games.join(', ')}</span>}
                  {generation === playing && <span className="tag here"> · Playing now</span>}
                </p>
                <div className="genbody">
                  <div className="pickers">
                    {KINDS.map(([kind, label]) => {
                      const options = setsFor(character, generation, kind);
                      if (options.length === 0) return null;
                      return (
                        <div key={kind} className="group">
                          <p className="kind">{label}</p>
                          <div className="tiles">
                            {options.map((set) => (
                              <button
                                key={set.id}
                                type="button"
                                className={`tile${chosen[kind]?.id === set.id ? ' on' : ''}`}
                                aria-pressed={chosen[kind]?.id === set.id}
                                title={set.title}
                                onClick={() => pickSet(generation, kind, set)}
                              >
                                <Sprite set={set} size={kind === 'overworld' ? 88 : 108} />
                                <small>
                                  {set.title}
                                  {set.generation !== generation && ` (Gen ${NUMERALS[set.generation] ?? set.generation})`}
                                </small>
                              </button>
                            ))}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                  <div className="combo" aria-label="Current selection">
                    {KINDS.map(([kind, label]) => (
                      <figure key={kind}>
                        <Sprite set={chosen[kind]} size={kind === 'overworld' ? 128 : 172} title={chosen[kind]?.title ?? "The game's own"} />
                        <figcaption>{label}</figcaption>
                      </figure>
                    ))}
                  </div>
                </div>
              </div>
            );
          })}
          <p className="muted" style={{ fontSize: 13 }}>
            In the other generations {character.name} has no sprites yet, and the game's own protagonist is kept. Only sprites from the official
            games are included for now.
          </p>
        </div>
      ) : (
        <p className="muted">Choose a trainer above to set their sprites for each generation.</p>
      )}
    </section>
  );
}