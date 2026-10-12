import { useState } from 'react';
import { api } from '../api/client';
import type { Catalog, RegionId } from '../api/types';
import { portraitOf } from '../components/looks';
import { Sprite } from '../components/Sprite';
import { regionStyle } from '../components/region';

const COMING = ['Kalos', 'Alola', 'Galar', 'Paldea'];

type Mode = 'new' | 'import';

/**
 * A new trainer: either one about to start a game, or one built from a save the player already
 * has. Calls onCreated with the trainer's id once they exist and are the active trainer.
 */
export function NewTrainer({
  catalog,
  onCreated,
  onCancel,
}: {
  catalog: Catalog;
  /** started: a game was asked to start; notStarted: why it couldn't. */
  onCreated: (trainerId: string, started: boolean, notStarted?: string) => void;
  onCancel: () => void;
}) {
  const [mode, setMode] = useState<Mode>('new');
  const [region, setRegion] = useState<RegionId>();
  const [gameId, setGameId] = useState<string>();
  const [character, setCharacter] = useState<string>();
  const [file, setFile] = useState<File>();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();

  const games = region ? catalog.games.filter((game) => game.region === region && game.playable) : [];
  const game = games.find((g) => g.id === gameId) ?? games[0];

  const pickRegion = (next: RegionId) => {
    setRegion(next);
    setGameId(undefined);
    setProblem(undefined);
  };

  const create = async () => {
    if (mode === 'new') {
      if (!region) return setProblem('Pick a home region first.');
    } else if (!file) {
      return setProblem('Choose a save file first.');
    }

    setBusy(true);
    setProblem(undefined);
    try {
      // A trainer's name is the one in their save: read from an imported one, and for a new game
      // chosen in the game. Until then they go by a placeholder the backend knows to replace.
      const firstName = mode === 'import' && file ? file.name.replace(/\.[^.]+$/, '') : 'New trainer';
      const created = await api.createTrainer(firstName, mode === 'new' ? game?.id : undefined, mode === 'new' ? region : undefined);
      const id = created.createdProfileId;
      if (!id) throw new Error("The trainer wasn't created");

      if (mode === 'import' && file) await api.importSave(id, file);
      if (character) await api.setAppearance(id, { character });
      await api.switchTrainer(id);

      if (mode === 'import') {
        const trainer = (await api.journey()).trainers.find((t) => t.id === id);
        // Bringing a save in is one of the moments a title is taken: a save that has already
        // beaten its League makes its trainer champion.
        if (trainer?.currentSaveId !== undefined) await api.syncSave(trainer.currentSaveId, true);
        if (trainer && trainer.trainerName !== trainer.name) await api.renameTrainer(id, trainer.trainerName);
        onCreated(id, false);
        return;
      }

      // A new game starts right away. If it can't (no ROM or emulator set yet), the trainer
      // exists all the same and the reason is shown on their card.
      let reason: string | undefined;
      await api.play().catch((e: unknown) => {
        reason = e instanceof Error ? e.message : String(e);
      });
      onCreated(id, true, reason);
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const option = (on: boolean, pick: () => void, inner: React.ReactNode, extra = '', style?: React.CSSProperties) => (
    <button type="button" className={`opt ${extra}${on ? ' on' : ''}`} aria-pressed={on} onClick={pick} disabled={busy} style={style}>
      {inner}
    </button>
  );

  return (
    <section className="panel newp">
      <div className="dexhead">
        <h2>New trainer profile</h2>
        <button type="button" className="btn" onClick={onCancel} aria-label="Close" disabled={busy}>
          ✕
        </button>
      </div>

      <div className="group">
        <p className="eyebrow">How should this trainer start?</p>
        <div className="optgrid two">
          {option(
            mode === 'new',
            () => setMode('new'),
            <>
              <b>Start new game</b>
              <small>The game starts right away. The first save made in it becomes this trainer.</small>
            </>,
            'big',
          )}
          {option(
            mode === 'import',
            () => setMode('import'),
            <>
              <b>Import existing save</b>
              <small>Pick a save file. The trainer is built from its data.</small>
            </>,
            'big',
          )}
        </div>
      </div>

      {mode === 'import' ? (
        <div className="group">
          <p className="eyebrow">Save file</p>
          <label className="opt big" style={{ cursor: 'pointer' }}>
            <b>{file ? file.name : 'Choose a save file'}</b>
            <small>Name, home region, badges and party are read from it.</small>
            <input
              type="file"
              hidden
              disabled={busy}
              onChange={(e) => {
                setFile(e.target.files?.[0]);
                setProblem(undefined);
              }}
            />
          </label>
        </div>
      ) : (
        <>
          <div className="group">
            <p className="eyebrow">Home region</p>
            <div className="optgrid three">
              {catalog.regions.map((r) =>
                option(region === r.id, () => pickRegion(r.id), <b>{r.name}</b>, 'rg', regionStyle(r.id)),
              )}
              {COMING.map((soon) => (
                <div key={soon} className="opt soon">
                  <b>{soon}</b>
                  <small>Coming soon</small>
                </div>
              ))}
            </div>
          </div>
          {games.length > 1 && (
            <div className="group">
              <p className="eyebrow">Game</p>
              <div className="optgrid three">
                {games.map((g) => option(game?.id === g.id, () => setGameId(g.id), <b>{g.name}</b>))}
              </div>
            </div>
          )}
        </>
      )}

      <div className="group">
        <p className="eyebrow">Trainer</p>
        <div className="optgrid looks">
          {catalog.characters.map((c) =>
            option(
              character === c.id,
              () => setCharacter(character === c.id ? undefined : c.id),
              <>
                <Sprite set={portraitOf(catalog, c.id, undefined, game?.generation)} size={44} />
                <b>{c.name}</b>
              </>,
              'look',
            ),
          )}
        </div>
        <p className="muted" style={{ fontSize: 13 }}>
          Cosmetic only, and optional: without one, each game's own protagonist is kept.
        </p>
      </div>

      {problem && <p className="err">{problem}</p>}
      <div className="optgrid two">
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>
          Cancel
        </button>
        <button type="button" className="btn go" onClick={create} disabled={busy}>
          {busy ? 'Creating…' : mode === 'import' ? 'Import save' : 'Start game'}
        </button>
      </div>
    </section>
  );
}