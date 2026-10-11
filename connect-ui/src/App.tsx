import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from './api/client';
import type { Catalog, Journey, Trainer } from './api/types';
import { Roster } from './screens/Roster';
import { TrainerCard } from './screens/TrainerCard';

const TABS = [
  ['card', 'Trainer card'],
  ['dex', 'Pokédex'],
  ['pc', 'PC'],
  ['travel', 'Travel'],
  ['journal', 'Journal'],
  ['league', 'League'],
] as const;
type Tab = (typeof TABS)[number][0];

function messageOf(error: unknown): string {
  return error instanceof ApiError || error instanceof Error ? error.message : String(error);
}

export function App() {
  const [catalog, setCatalog] = useState<Catalog>();
  const [journey, setJourney] = useState<Journey>();
  const [error, setError] = useState<string>();
  const [tab, setTab] = useState<Tab>('card');
  const [screen, setScreen] = useState<'main' | 'roster'>('main');
  const [busy, setBusy] = useState(false);

  // A save the trainer is in is brought into step with them before they are shown: badges
  // earned in another save of the same region belong on this card too.
  const load = useCallback(async () => {
    let next = await api.journey();
    const active = next.trainers.find((trainer) => trainer.isActive);
    if (active?.currentSaveId !== undefined) {
      await api.syncSave(active.currentSaveId).catch(() => undefined);
      next = await api.journey();
    }
    setJourney(next);
  }, []);

  useEffect(() => {
    Promise.all([api.catalog().then(setCatalog), load()]).catch((e) => setError(messageOf(e)));
  }, [load]);

  const pick = useCallback(
    async (trainer: Trainer) => {
      setBusy(true);
      setError(undefined);
      try {
        if (!trainer.isActive) await api.switchTrainer(trainer.id);
        await load();
        setScreen('main');
        setTab('card');
      } catch (e) {
        setError(messageOf(e));
      } finally {
        setBusy(false);
      }
    },
    [load],
  );

  const trainer = journey?.trainers.find((t) => t.isActive);
  const tint = trainer?.currentRegion ?? trainer?.homeRegion ?? 'sinnoh';

  return (
    <div className="app rg" style={{ '--region': `var(--${tint})` } as React.CSSProperties}>
      <div className="top">
        <div className="brand">
          Pokémon <span>Connect</span>
        </div>
        {journey && trainer && (
          <button
            type="button"
            className="switch"
            onClick={() => setScreen(screen === 'main' ? 'roster' : 'main')}
            aria-label={`Switch trainer, current trainer ${trainer.trainerName}`}
          >
            <span className="av">{trainer.trainerName.slice(0, 1)}</span>
            {trainer.trainerName}
            <span className="sub">{journey.trainers.length} trainers ▾</span>
          </button>
        )}
      </div>

      {screen === 'main' && (
        <div className="tabs" role="tablist">
          {TABS.map(([id, label]) => (
            <button key={id} type="button" role="tab" aria-selected={id === tab} onClick={() => setTab(id)}>
              {label}
            </button>
          ))}
        </div>
      )}

      <main className="view">
        {error && <div className="error">{error}</div>}
        {!error && (!catalog || !journey) && <div className="note">Loading…</div>}

        {catalog && journey && screen === 'roster' && (
          <Roster trainers={journey.trainers} catalog={catalog} busy={busy} onPick={pick} onBack={() => setScreen('main')} />
        )}

        {catalog && journey && screen === 'main' && !trainer && (
          <Roster trainers={journey.trainers} catalog={catalog} busy={busy} onPick={pick} onBack={() => undefined} />
        )}

        {catalog && trainer && screen === 'main' && tab === 'card' && <TrainerCard trainer={trainer} catalog={catalog} />}

        {catalog && trainer && screen === 'main' && tab !== 'card' && (
          <section className="panel">
            <p className="eyebrow">{TABS.find(([id]) => id === tab)?.[1]}</p>
            <h2>Not built yet</h2>
            <p className="muted">This screen comes in a later step. The trainer card and the roster are the first two.</p>
          </section>
        )}
      </main>
    </div>
  );
}