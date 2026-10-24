import { useEffect, type ReactNode } from 'react';
import type { Names, StoredMon } from '../api/types';
import { MonSprite } from './Mon';

// Type numbers count from 1; each with the colour the games give it.
const TYPES: [name: string, colour: string][] = [
  ['Normal', '#9FA19F'], ['Fighting', '#E0862B'], ['Flying', '#81B9EF'], ['Poison', '#9141CB'], ['Ground', '#915121'],
  ['Rock', '#AFA981'], ['Bug', '#91A119'], ['Ghost', '#704170'], ['Steel', '#60A1B8'], ['Fire', '#E62829'],
  ['Water', '#2980EF'], ['Grass', '#3FA129'], ['Electric', '#E3B00B'], ['Psychic', '#EF4179'], ['Ice', '#3DCEF3'],
  ['Dragon', '#5060E1'], ['Dark', '#624D4E'], ['Fairy', '#EF70EF'],
];

const STATS = ['HP', 'Attack', 'Defense', 'Sp. Atk', 'Sp. Def', 'Speed'];
// A nature raises one of Attack, Defense, Speed, Sp. Atk, Sp. Def and lowers another; these are their places in STATS.
const NATURE_STATS = [1, 2, 5, 3, 4];

function natureEffect(nature: number | undefined): { up?: number; down?: number } {
  if (nature === undefined || nature < 0 || nature > 24) return {};
  const up = NATURE_STATS[Math.floor(nature / 5)];
  const down = NATURE_STATS[nature % 5];
  return up === down ? {} : { up, down };
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="mdfact">
      <span>{label}</span>
      <b>{children}</b>
    </div>
  );
}

/**
 * Everything about one Pokémon, in a window over the page. Closed with its button, the Escape
 * key, or a click outside it. <paramref name="actions"/> are what the screen it was opened from
 * lets the player do with it.
 */
export function MonDetails({ mon, names, onClose, actions }: { mon: StoredMon; names: Names | undefined; onClose: () => void; actions?: ReactNode }) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const species = names?.species[mon.species] ?? `#${mon.species}`;
  const named = mon.isNicknamed && mon.nickname && mon.nickname.toLowerCase() !== species.toLowerCase();
  const title = mon.isEgg ? 'Egg' : named ? mon.nickname : species;
  const { up, down } = natureEffect(mon.nature);
  const stats = mon.stats ?? [];
  const highest = Math.max(1, ...stats);
  // The Game Boy games' values run to 15, not 31.
  const ivCap = (mon.generation ?? 3) <= 2 ? 15 : 31;
  const moves = (mon.moves ?? []).filter((move) => move > 0);

  return (
    <div className="mdback" onClick={onClose}>
      <div className="md" role="dialog" aria-modal="true" aria-label={`${title}, details`} onClick={(event) => event.stopPropagation()}>
        <div className="mdhead">
          <div className="mdpic">
            <MonSprite species={mon.isEgg ? 0 : mon.species} size={96} fallback={title} shiny={mon.isShiny} />
          </div>
          <div className="mdname">
            <p className="px">No. {String(mon.species).padStart(3, '0')}</p>
            <h2>
              {title}
              {mon.gender === 0 && <span className="male"> ♂</span>}
              {mon.gender === 1 && <span className="female"> ♀</span>}
              {mon.isShiny && <span className="shiny" title="Shiny"> ★</span>}
            </h2>
            <p>
              {named && !mon.isEgg ? `${species} · ` : ''}Lv. {mon.level}
            </p>
            <div className="mdtypes">
              {(mon.types ?? []).map((type) => {
                const entry = TYPES[type - 1];
                return entry ? (
                  <span key={type} style={{ background: entry[1] }}>
                    {entry[0]}
                  </span>
                ) : null;
              })}
            </div>
          </div>
          <button type="button" className="mdclose" onClick={onClose} aria-label="Close">
            ×
          </button>
        </div>

        <div className="mdbody">
          {mon.levelUpPercent !== undefined && mon.level < 100 && !mon.isEgg && (
            <div className="mdexp">
              <div className="bar" aria-hidden="true">
                <i style={{ width: `${Math.round(mon.levelUpPercent * 100)}%` }} />
              </div>
              <span>{mon.expToLevelUp?.toLocaleString()} EXP to the next level</span>
            </div>
          )}

          <div className="mdfacts">
            {mon.nature !== undefined && (mon.generation ?? 3) >= 3 && <Fact label="Nature">{names?.natures?.[mon.nature] ?? '—'}</Fact>}
            {!!mon.ability && <Fact label="Ability">{names?.abilities?.[mon.ability] ?? `#${mon.ability}`}</Fact>}
            <Fact label="Held item">{mon.heldItem ? names?.items?.[mon.heldItem] ?? `#${mon.heldItem}` : 'None'}</Fact>
            {mon.friendship !== undefined && <Fact label="Friendship">{mon.friendship}</Fact>}
            {mon.originTrainerName && (
              <Fact label="Original trainer">
                {mon.originTrainerName}
                {mon.tid !== undefined && <small> · {String(mon.tid).padStart(5, '0')}</small>}
              </Fact>
            )}
            {mon.originMetLocation && (
              <Fact label="Met">
                {mon.originMetLocation}
                {mon.originMetLevel ? <small> · Lv. {mon.originMetLevel}</small> : null}
              </Fact>
            )}
          </div>

          {stats.length === 6 && (
            <div className="mdstats">
              <div className="mdstat head">
                <span>Stats</span>
                <span />
                <b />
                <small>IV</small>
                <small>EV</small>
              </div>
              {STATS.map((label, index) => (
                <div key={label} className={`mdstat${index === up ? ' up' : index === down ? ' down' : ''}`}>
                  <span>
                    {label}
                    {index === up ? ' +' : index === down ? ' −' : ''}
                  </span>
                  <div className="bar" aria-hidden="true">
                    <i style={{ width: `${((stats[index] ?? 0) / highest) * 100}%` }} />
                  </div>
                  <b>{stats[index]}</b>
                  <small className={mon.iVs?.[index] === ivCap ? 'best' : ''}>{mon.iVs?.[index] ?? '–'}</small>
                  <small>{mon.eVs?.[index] ?? '–'}</small>
                </div>
              ))}
            </div>
          )}

          {moves.length > 0 && (
            <div>
              <p className="kind">Moves</p>
              <div className="mdmoves">
                {moves.map((move, index) => (
                  <span key={index}>{names?.moves[move] ?? `Move #${move}`}</span>
                ))}
              </div>
            </div>
          )}
        </div>

        {actions && <div className="mdactions">{actions}</div>}
      </div>
    </div>
  );
}
