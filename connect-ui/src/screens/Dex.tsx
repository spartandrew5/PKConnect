import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../api/client';
import type { Catalog, DexEntry, Names, Trainer } from '../api/types';
import { useLoad } from '../api/useLoad';
import { MonSprite } from '../components/Mon';

type Order = 'number' | 'name';

/** What a tile's corner shows: caught, seen, or neither. */
function Mark({ state }: { state: 'caught' | 'seen' | 'unk' }) {
  const common = { viewBox: '0 0 24 24', width: 14, height: 14, fill: 'none', stroke: 'currentColor', strokeWidth: 3, strokeLinecap: 'round', strokeLinejoin: 'round' } as const;
  if (state === 'caught')
    return (
      <svg {...common} aria-hidden="true">
        <path d="M5 12.5l4.5 4.5L19 7.5" />
      </svg>
    );
  if (state === 'seen')
    return (
      <svg {...common} strokeWidth={2.5} aria-hidden="true">
        <path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7-10-7-10-7Z" />
        <circle cx="12" cy="12" r="3" />
      </svg>
    );
  return (
    <svg {...common} strokeWidth={2} aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M5.6 5.6l12.8 12.8" />
    </svg>
  );
}

/** One species. Its picture is only asked for once the tile scrolls into view: a National Pokédex is hundreds of them. */
function Tile({ entry, name, pad, onPoint }: { entry: DexEntry; name: string; pad: number; onPoint: (entry: DexEntry | undefined) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const [near, setNear] = useState(false);
  const state = entry.caught ? 'caught' : entry.seen ? 'seen' : 'unk';

  useEffect(() => {
    const tile = ref.current;
    if (!tile || near) return;
    const watcher = new IntersectionObserver(
      (seen) => {
        if (seen.some((s) => s.isIntersecting)) setNear(true);
      },
      { rootMargin: '300px' },
    );
    watcher.observe(tile);
    return () => watcher.disconnect();
  }, [near]);

  return (
    <div
      ref={ref}
      className={`dxt ${state}`}
      title={state === 'unk' ? undefined : name}
      onMouseEnter={() => onPoint(entry)}
      onMouseLeave={() => onPoint(undefined)}
    >
      <span className="dxno">#{String(entry.number).padStart(pad, '0')}</span>
      <span className="dxmark">
        <Mark state={state} />
      </span>
      {near && <MonSprite species={entry.species} size={96} fallback={state === 'unk' ? '' : name} />}
    </div>
  );
}

export function Dex({ trainer, catalog, names }: { trainer: Trainer; catalog: Catalog; names: Names | undefined }) {
  const [scope, setScope] = useState<'regional' | 'national'>('regional');
  const [search, setSearch] = useState('');
  const [order, setOrder] = useState<Order>('number');
  const [descending, setDescending] = useState(false);
  const [pointed, setPointed] = useState<DexEntry>();
  const { data, error } = useLoad(() => api.dex(scope), [trainer.id, trainer.currentSaveId, scope]);
  const region = catalog.regions.find((r) => r.id === trainer.currentRegion);

  const nameOf = (entry: DexEntry) => names?.species[entry.species] ?? `#${entry.species}`;
  const entries = data?.entries;
  const shown = useMemo(() => {
    if (!entries) return [];
    const wanted = search.trim().toLowerCase().replace(/^#/, '');
    const known = (entry: DexEntry) => entry.seen || entry.caught;
    const name = (entry: DexEntry) => names?.species[entry.species] ?? '';
    // A species not yet seen has no name to find it by, only its number.
    const list = entries.filter(
      (entry) => !wanted || String(entry.number).includes(wanted) || (known(entry) && name(entry).toLowerCase().includes(wanted)),
    );
    list.sort((a, b) => (order === 'name' ? name(a).localeCompare(name(b)) || a.number - b.number : a.number - b.number));
    return descending ? list.reverse() : list;
  }, [entries, names, search, order, descending]);

  if (error) return <div className="error">{error}</div>;

  const national = data ? data.scope === 'national' : scope === 'national';
  const count = entries?.length ?? 0;
  const pad = count > 999 ? 4 : 3;

  return (
    <section className="dexwin">
      <div className="dexbar">
        <span className="dexlens" aria-hidden="true" />
        <div className="dextitle">
          <h2>{national ? 'National Pokédex' : `${region?.name ?? 'Regional'} Pokédex`}</h2>
          <div className="dexchips">
            <span>
              Seen {data?.seen ?? '–'} / {count}
            </span>
            <span>
              Owned {data?.caught ?? '–'} / {count}
            </span>
          </div>
        </div>
        <div className="dextools">
          <div className="dexseg" role="group" aria-label="Pokédex">
            <button type="button" aria-pressed={scope === 'regional'} onClick={() => setScope('regional')}>
              Regional
            </button>
            <button type="button" aria-pressed={scope === 'national'} onClick={() => setScope('national')}>
              National
            </button>
          </div>
          <label className="dexsearch">
            <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
              <circle cx="11" cy="11" r="7" />
              <path d="M20 20l-3.5-3.5" />
            </svg>
            <input type="search" placeholder="Search…" value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search the Pokédex" />
          </label>
          <button type="button" className="dexbtn" onClick={() => setOrder(order === 'number' ? 'name' : 'number')} aria-label="Order by number or name">
            {order === 'number' ? 'No.' : 'A–Z'}
          </button>
          <button type="button" className="dexbtn" onClick={() => setDescending(!descending)} aria-label={descending ? 'Descending' : 'Ascending'}>
            {descending ? '↑' : '↓'}
          </button>
        </div>
      </div>

      <div className="dexscroll">
        {!data && <p className="muted">Loading…</p>}
        {data && shown.length === 0 && <p className="muted">Nothing matches “{search}”.</p>}
        <div className="dexgrid">
          {shown.map((entry) => (
            <Tile key={entry.species} entry={entry} name={nameOf(entry)} pad={pad} onPoint={setPointed} />
          ))}
        </div>
      </div>

      <div className="dexfoot">
        {pointed
          ? `#${String(pointed.number).padStart(pad, '0')} · ${pointed.seen || pointed.caught ? nameOf(pointed) : '?????'} · ${pointed.caught ? 'Caught' : pointed.seen ? 'Seen' : 'Not yet seen'}`
          : national
            ? `${trainer.trainerName}'s National Pokédex`
            : `${trainer.trainerName}'s Regional Pokédex`}
      </div>
    </section>
  );
}
