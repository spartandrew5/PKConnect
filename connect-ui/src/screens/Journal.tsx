import { useState } from 'react';
import { api } from '../api/client';
import type { Catalog, JournalEntry, Trainer } from '../api/types';
import { useLoad } from '../api/useLoad';
import { regionStyle, shortDate } from '../components/region';
import { NotebookPen } from 'lucide-react';

const KINDS: [JournalEntry['type'] | 'all', string][] = [
  ['all', 'Everything'],
  ['badge', 'Badges'],
  ['title', 'Titles'],
  ['travel', 'Travel'],
];

export function Journal({ trainer, catalog }: { trainer: Trainer; catalog: Catalog }) {
  const [filter, setFilter] = useState<JournalEntry['type'] | 'all'>('all');
  const { data, error } = useLoad(() => api.journal(trainer.id), [trainer.id]);
  const entries = (data ?? []).filter((entry) => filter === 'all' || entry.type === filter);

  return (
    <section className="panel">
      <div>
        <p className="eyebrow ilabel">
          <NotebookPen className="ico" size={14} aria-hidden="true" />
          Journal
        </p>
        <h2>{trainer.trainerName}'s journey so far</h2>
      </div>
      <div className="chips">
        {KINDS.map(([kind, label]) => (
          <button key={kind} type="button" aria-pressed={filter === kind} onClick={() => setFilter(kind)}>
            {label}
          </button>
        ))}
      </div>
      {error && <div className="error">{error}</div>}
      {!error && !data && <p className="muted">Loading…</p>}
      {data && (
        <div className="rows">
          {entries.length === 0 && (
            <p className="muted">{data.length === 0 ? 'Nothing written yet. Entries appear as things happen.' : 'No entries of this kind yet.'}</p>
          )}
          {entries.map((entry, index) => (
            <div key={index} className="row rg" style={regionStyle(entry.region)}>
              <span className="when">{shortDate(entry.at)}</span>
              <span className={`mark${entry.type === 'title' ? '' : ' hollow'}`} />
              <div className="grow">
                {entry.text}
                {entry.region && (
                  <div className="muted" style={{ fontSize: 13 }}>
                    {catalog.regions.find((region) => region.id === entry.region)?.name}
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}