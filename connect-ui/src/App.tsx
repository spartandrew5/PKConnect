import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from './api/client';
import type { BoardingResult, Catalog, Journey, Names, PlayStatus, Trainer } from './api/types';
import { PORTS } from './components/region';
import { Appearance } from './screens/Appearance';
import { Dex } from './screens/Dex';
import { PC } from './screens/PC';
import { Journal } from './screens/Journal';
import { League } from './screens/League';
import { NewTrainer } from './screens/NewTrainer';
import { Roster } from './screens/Roster';
import { Settings } from './screens/Settings';
import { TrainerCard } from './screens/TrainerCard';
import { Travel } from './screens/Travel';

const BUILT: readonly string[] = ['card', 'dex', 'pc', 'travel', 'journal', 'league'];

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
  const [names, setNames] = useState<Names>();
  const [toast, setToast] = useState<string>();
  // The tab is kept in the address, so a screen can be linked to and survives a reload.
  const [tab, setTabState] = useState<Tab>(() => {
    const hash = location.hash.slice(1);
    return TABS.some(([id]) => id === hash) ? (hash as Tab) : 'card';
  });
  const setTab = useCallback((next: Tab) => {
    setTabState(next);
    history.replaceState(null, '', `#${next}`);
  }, []);
  const [screen, setScreen] = useState<'main' | 'roster' | 'new' | 'settings' | 'look'>(() => {
    const hash = location.hash.slice(1);
    return hash === 'roster' || hash === 'new' || hash === 'settings' || hash === 'look' ? hash : 'main';
  });
  const [play, setPlay] = useState<PlayStatus>();

  useEffect(() => {
    if (!toast) return;
    const timer = setTimeout(() => setToast(undefined), 4000);
    return () => clearTimeout(timer);
  }, [toast]);
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
    // Names only dress up what is shown; a screen works without them.
    api.names().then(setNames, () => undefined);
  }, [load]);

  const arrived = useCallback(
    async (result: BoardingResult) => {
      await load().catch((e) => setError(messageOf(e)));
      setTab('card');
      const stayed = result.stayed.length > 0 ? `, ${result.stayed.length} stayed behind` : '';
      setToast(`Arrived in ${PORTS[result.region].port} with ${result.travelled.length} Pokémon${stayed}`);
    },
    [load, setTab],
  );

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
    [load, setTab],
  );

  // While a game runs the backend is asked now and then whether the player is back; when they
  // are, everything shown is read again: the save has changed, and maybe a title with it.
  const running = play?.state === 'running';
  useEffect(() => {
    api.playStatus().then(setPlay, () => undefined);
  }, []);
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(async () => {
      const next = await api.playStatus().catch(() => undefined);
      if (!next || next.state === 'running') return;
      setPlay(next);
      await load().catch((e) => setError(messageOf(e)));
      if (next.state === 'failed') setError(next.message);
      else if (next.message) setToast(next.message);
    }, 2000);
    return () => clearInterval(timer);
  }, [running, load]);

  const startGame = useCallback(async () => {
    setError(undefined);
    try {
      setPlay(await api.play());
    } catch (e) {
      setError(messageOf(e));
    }
  }, []);

  const created = useCallback(
    async (_trainerId: string, started: boolean, notStarted?: string) => {
      setError(undefined);
      await load().catch((e) => setError(messageOf(e)));
      setScreen('main');
      setTab('card');
      if (notStarted) setError(`The trainer was created, but the game didn't start: ${notStarted}`);
      else if (started) setPlay(await api.playStatus().catch(() => undefined));
      else setToast('Trainer created');
    },
    [load, setTab],
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
        <button type="button" className="btn" onClick={() => setScreen(screen === 'settings' ? 'main' : 'settings')}>
          Settings
        </button>
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
          <Roster
            trainers={journey.trainers}
            catalog={catalog}
            busy={busy}
            onPick={pick}
            onBack={() => setScreen('main')}
            onNew={() => setScreen('new')}
          />
        )}

        {catalog && screen === 'new' && <NewTrainer catalog={catalog} onCreated={created} onCancel={() => setScreen('roster')} />}

        {catalog && journey && screen === 'main' && !trainer && (
          <Roster
            trainers={journey.trainers}
            catalog={catalog}
            busy={busy}
            onPick={pick}
            onBack={() => undefined}
            onNew={() => setScreen('new')}
          />
        )}

        {catalog && screen === 'settings' && <Settings catalog={catalog} onDone={() => setScreen('main')} />}

        {catalog && trainer && screen === 'main' && tab === 'card' && (
          <TrainerCard
            trainer={trainer}
            catalog={catalog}
            playing={running && play?.trainerId === trainer.id}
            onPlay={startGame}
            onLook={() => setScreen('look')}
          />
        )}

        {catalog && trainer && screen === 'look' && (
          <Appearance
            trainer={trainer}
            catalog={catalog}
            onChanged={() => void load().catch((e) => setError(messageOf(e)))}
            onDone={() => setScreen('main')}
          />
        )}

        {catalog && trainer && screen === 'main' && tab === 'dex' && <Dex trainer={trainer} catalog={catalog} names={names} />}
        {catalog && trainer && screen === 'main' && tab === 'pc' && (
          <PC trainer={trainer} catalog={catalog} names={names} onSaved={() => void load().catch((e) => setError(messageOf(e)))} />
        )}
        {catalog && trainer && screen === 'main' && tab === 'journal' && <Journal trainer={trainer} catalog={catalog} />}
        {catalog && journey && trainer && screen === 'main' && tab === 'league' && (
          <League trainer={trainer} journey={journey} catalog={catalog} names={names} />
        )}
        {catalog && trainer && screen === 'main' && tab === 'travel' && (
          <Travel trainer={trainer} catalog={catalog} names={names} onArrived={arrived} />
        )}

        {catalog && trainer && screen === 'main' && !BUILT.includes(tab) && (
          <section className="panel">
            <p className="eyebrow">{TABS.find(([id]) => id === tab)?.[1]}</p>
            <h2>Not built yet</h2>
            <p className="muted">This screen comes in a later step.</p>
          </section>
        )}
      </main>
      {toast && (
        <div className="toast" role="status">
          {toast}
        </div>
      )}
    </div>
  );
}