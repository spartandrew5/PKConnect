import { useState } from 'react';
import { api } from '../api/client';
import type { Catalog, Names, RegionId, Trainer } from '../api/types';
import { useLoad } from '../api/useLoad';
import { MonSprite } from '../components/Mon';
import { regionStyle } from '../components/region';

// The region a species was first found in, for its tint.
function homeOf(species: number): RegionId {
  return species <= 151 ? 'kanto' : species <= 251 ? 'johto' : species <= 386 ? 'hoenn' : species <= 493 ? 'sinnoh' : 'unova';
}

export function Dex({ trainer, catalog, names }: { trainer: Trainer; catalog: Catalog; names: Names | undefined }) {
  const [scope, setScope] = useState<'regional' | 'national'>('regional');
  const { data, error } = useLoad(() => api.dex(scope), [trainer.id, trainer.currentSaveId, scope]);
  const region = catalog.regions.find((r) => r.id === trainer.currentRegion);

  if (error) return <div className="error">{error}</div>;

  const national = data ? data.scope === 'national' : scope === 'national';
  const count = data?.entries.length ?? 0;
  const pad = count > 999 ? 4 : 3;

  return (
    <section className="panel">
      <div className="dexhead">
        <div>
          <p className="eyebrow">Pokédex</p>
          <h2>{national ? 'National Pokédex' : `${region?.name ?? 'Regional'} Pokédex`}</h2>
        </div>
        <div className="seg" role="group" aria-label="Pokédex">
          <button type="button" aria-pressed={scope === 'regional'} onClick={() => setScope('regional')}>
            {region?.name ?? 'Regional'}
          </button>
          <button type="button" aria-pressed={scope === 'national'} onClick={() => setScope('national')}>
            National
          </button>
        </div>
      </div>

      {!data && <p className="muted">Loading…</p>}
      {data && (
        <>
          <div className="facts">
            <span>
              <b>{data.seen}</b> seen
            </span>
            <span>
              <b>{data.caught}</b> caught
            </span>
            <span>of {count}</span>
          </div>
          <div className="bar" aria-hidden="true">
            <i style={{ width: `${count ? (data.caught / count) * 100 : 0}%` }} />
            <i className="seen" style={{ width: `${count ? ((data.seen - data.caught) / count) * 100 : 0}%` }} />
          </div>
          <div className="dexgrid">
            {data.entries.map((entry) => {
              const state = entry.caught ? 'caught' : entry.seen ? 'seen' : 'unk';
              const name = names?.species[entry.species] ?? `#${entry.species}`;
              return (
                <div key={entry.species} className={`dx rg ${state}`} style={regionStyle(homeOf(entry.species))}>
                  <span className="px">No. {String(entry.number).padStart(pad, '0')}</span>
                  {state !== 'unk' && (
                    <span className={`dxsprite${entry.caught ? '' : ' shade'}`}>
                      <MonSprite species={entry.species} size={48} fallback={name} />
                    </span>
                  )}
                  <b>{state === 'unk' ? '?????' : name}</b>
                  <small>{entry.caught ? 'Caught' : entry.seen ? 'Seen' : 'Not yet seen'}</small>
                </div>
              );
            })}
          </div>
          <p className="muted" style={{ fontSize: 13 }}>
            {national
              ? `Numbered in national order and tinted by home region. Counts every save ${trainer.trainerName} has and what is kept in storage.`
              : `Numbered in ${region?.name ?? 'the region'}'s own order, as ${trainer.trainerName}'s save there has it.`}
          </p>
        </>
      )}
    </section>
  );
}