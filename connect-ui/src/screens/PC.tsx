import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import type { Catalog, Names, StorageBox, StoredMon, Trainer } from '../api/types';
import { MonSprite } from '../components/Mon';

type Where = 'party' | 'game' | 'vault';

/** A slot somewhere: in the party, in one of the game's boxes, or in a box of main storage. */
interface Slot {
  where: Where;
  boxId: string;
  slot: number;
}

interface Storage {
  gameBoxes: StorageBox[];
  vaultBoxes: StorageBox[];
  gameMons: StoredMon[];
  vaultMons: StoredMon[];
  pending: number;
}

const PARTY_BOX = '-1';
const sameSlot = (a: Slot, b: Slot) => a.where === b.where && a.boxId === b.boxId && a.slot === b.slot;

/**
 * The PC: the party travelling with the trainer, the boxes of the game they are in, and main
 * storage, which every one of their games shares. Pick a Pokémon, then a slot, to move it.
 * Moves are PKVault's own: they wait until they are saved, and can be undone until then.
 */
export function PC({
  trainer,
  catalog,
  names,
  onSaved,
}: {
  trainer: Trainer;
  catalog: Catalog;
  names: Names | undefined;
  onSaved: () => void;
}) {
  const saveId = trainer.currentSaveId;
  const [storage, setStorage] = useState<Storage>();
  const [problem, setProblem] = useState<string>();
  const [picked, setPicked] = useState<Slot>();
  const [box, setBox] = useState<{ game: number; vault: number }>({ game: 0, vault: 0 });
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    if (saveId === undefined) return;
    const [gameBoxes, vaultBoxes, gameMons, vaultMons, pending] = await Promise.all([
      api.saveBoxes(saveId),
      api.mainBoxes(),
      api.saveMons(saveId),
      api.mainMons(),
      api.pendingChanges(),
    ]);
    const boxesOnly = (boxes: StorageBox[]) => boxes.filter((b) => b.type === 0).sort((a, b) => a.order - b.order);
    setStorage({
      gameBoxes: boxesOnly(gameBoxes),
      vaultBoxes: boxesOnly(vaultBoxes),
      gameMons,
      // One Pokémon may be kept as several variants in a slot; the main one stands for it.
      vaultMons: vaultMons.filter((mon) => mon.isMain !== false),
      pending: pending.length,
    });
  }, [saveId]);

  useEffect(() => {
    setStorage(undefined);
    setPicked(undefined);
    load().catch((e) => setProblem(e instanceof Error ? e.message : String(e)));
  }, [load]);

  if (saveId === undefined) {
    return (
      <section className="panel">
        <p className="eyebrow">PC</p>
        <h2>No save yet</h2>
        <p>{trainer.trainerName} has no save to keep Pokémon in. Start the game first.</p>
      </section>
    );
  }
  if (!storage) return <div className={problem ? 'error' : 'note'}>{problem ?? 'Loading…'}</div>;

  const game = catalog.games.find((g) => g.id === trainer.currentGameId);
  const region = catalog.regions.find((r) => r.id === trainer.currentRegion);
  const gameBox = storage.gameBoxes[Math.min(box.game, storage.gameBoxes.length - 1)];
  const vaultBox = storage.vaultBoxes[Math.min(box.vault, storage.vaultBoxes.length - 1)];
  const party = storage.gameMons.filter((mon) => String(mon.boxId) === PARTY_BOX).sort((a, b) => a.boxSlot - b.boxSlot);

  const monAt = (at: Slot): StoredMon | undefined =>
    (at.where === 'vault' ? storage.vaultMons : storage.gameMons).find((mon) => String(mon.boxId) === at.boxId && mon.boxSlot === at.slot);
  const nameOf = (mon: StoredMon) => (mon.isEgg ? 'Egg' : names?.species[mon.species] ?? mon.nickname);

  const act = async (work: () => Promise<unknown>, after?: () => void) => {
    setBusy(true);
    setProblem(undefined);
    try {
      await work();
      after?.();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      await load().catch(() => undefined);
      setBusy(false);
    }
  };

  const click = (at: Slot) => {
    if (busy) return;
    const here = monAt(at);
    if (!picked) {
      if (here) setPicked(at);
      return;
    }
    const from = picked;
    setPicked(undefined);
    const moving = monAt(from);
    if (!moving || sameSlot(from, at)) return;

    // The party has no gaps: a Pokémon put past its end joins at the end.
    const slot = at.where === 'party' && !here ? Math.min(at.slot, party.length - (from.where === 'party' ? 1 : 0)) : at.slot;
    void act(() =>
      api.moveMon({
        pkmIds: [moving.id],
        sourceSaveId: from.where === 'vault' ? null : saveId,
        targetSaveId: at.where === 'vault' ? null : saveId,
        targetBoxId: at.boxId,
        targetBoxSlots: [slot],
        attached: false,
      }),
    );
  };

  const cell = (at: Slot, wide = false) => {
    const mon = monAt(at);
    const on = picked !== undefined && sameSlot(picked, at);
    return (
      <button
        key={`${at.where}-${at.boxId}-${at.slot}`}
        type="button"
        className={`slot${wide ? ' wide' : ''}${mon ? '' : ' empty'}${on ? ' sel' : ''}`}
        aria-pressed={on}
        aria-label={mon ? `${nameOf(mon)}, level ${mon.level}` : 'Empty slot'}
        title={mon ? `${nameOf(mon)} · Lv. ${mon.level}${mon.attachedSaveId ? ' · travelling' : ''}` : undefined}
        disabled={busy}
        onClick={() => click(at)}
      >
        {mon && (
          <>
            <MonSprite species={mon.isEgg ? 0 : mon.species} size={48} fallback={nameOf(mon)} />
            {wide && <span className="slotname">{nameOf(mon)}</span>}
            <small>Lv. {mon.level}</small>
          </>
        )}
      </button>
    );
  };

  const nav = (which: 'game' | 'vault', boxes: StorageBox[], current: StorageBox | undefined) => (
    <>
      <button type="button" aria-label="Previous box" disabled={boxes.length < 2} onClick={() => setBox({ ...box, [which]: (box[which] - 1 + boxes.length) % boxes.length })}>
        ‹
      </button>
      <span>{current?.name ?? 'No boxes'}</span>
      <button type="button" aria-label="Next box" disabled={boxes.length < 2} onClick={() => setBox({ ...box, [which]: (box[which] + 1) % boxes.length })}>
        ›
      </button>
    </>
  );

  const grid = (where: 'game' | 'vault', current: StorageBox | undefined) =>
    current ? (
      <div className="box">{Array.from({ length: current.slotCount }, (_, slot) => cell({ where, boxId: current.id, slot }))}</div>
    ) : (
      <p className="muted">Nothing here.</p>
    );

  const pickedMon = picked && monAt(picked);

  return (
    <>
      {storage.pending > 0 ? (
        <div className="note pending">
          <span>
            <b>
              {storage.pending} {storage.pending === 1 ? 'change' : 'changes'} not saved yet.
            </b>{' '}
            Nothing is written to the save or to storage until then.
          </span>
          <span className="actions">
            <button type="button" className="btn" disabled={busy} onClick={() => act(() => api.undoStorage())}>
              Undo all
            </button>
            <button type="button" className="btn go" disabled={busy} onClick={() => act(() => api.saveStorage(), onSaved)}>
              {busy ? 'Working…' : 'Save changes'}
            </button>
          </span>
        </div>
      ) : (
        <div className="note">
          {pickedMon ? (
            <>
              <b>{nameOf(pickedMon)}</b> Lv. {pickedMon.level} picked up. Choose a slot to move or swap it, or pick it again to put it down.
            </>
          ) : (
            'Pick a Pokémon, then pick a slot in any window to move it.'
          )}
        </div>
      )}
      {problem && <div className="error">{problem}</div>}

      <div className="pc">
        <section className="win win-party">
          <div className="win-bar">
            <span>Travelling party</span>
            <span className="sub muted">Goes with {trainer.trainerName} on the next trip</span>
            <span>{party.length} of 6</span>
          </div>
          <div className="win-body">
            <div className="partyrow">{Array.from({ length: 6 }, (_, slot) => cell({ where: 'party', boxId: PARTY_BOX, slot }, true))}</div>
          </div>
        </section>
        <section className="win win-game">
          <div className="win-bar">
            <span>{region?.name ?? 'Game'} PC</span>
            <span className="sub">{game?.name}</span>
            {nav('game', storage.gameBoxes, gameBox)}
          </div>
          <div className="win-body">{grid('game', gameBox)}</div>
        </section>
        <section className="win win-vault">
          <div className="win-bar">
            <span>National storage</span>
            <span className="sub">Shared by every game</span>
            {nav('vault', storage.vaultBoxes, vaultBox)}
          </div>
          <div className="win-body">{grid('vault', vaultBox)}</div>
        </section>
      </div>
    </>
  );
}