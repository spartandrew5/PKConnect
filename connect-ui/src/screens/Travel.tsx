import { useState } from 'react';
import { api } from '../api/client';
import type { BoardingResult, Catalog, Names, Trainer } from '../api/types';
import { useLoad } from '../api/useLoad';
import { MonSprite } from '../components/Mon';
import { listOf, PORTS, regionStyle } from '../components/region';

export function Travel({
  trainer,
  catalog,
  names,
  onArrived,
}: {
  trainer: Trainer;
  catalog: Catalog;
  names: Names | undefined;
  onArrived: (result: BoardingResult) => void;
}) {
  const { data, error } = useLoad(() => api.travelPreview(), [trainer.id, trainer.currentGameId]);
  const [picked, setPicked] = useState<string>();
  const [boarding, setBoarding] = useState(false);
  const [refused, setRefused] = useState<string>();

  if (error) return <div className="error">{error}</div>;
  if (!data) return <div className="note">Loading…</div>;

  const regionName = (id: string | undefined) => catalog.regions.find((r) => r.id === id)?.name ?? 'somewhere';
  const from = regionName(data.fromRegion);

  if (trainer.party.length === 0) {
    return (
      <section className="panel">
        <p className="eyebrow">Travel</p>
        <h2>Nobody to travel with yet</h2>
        <p>{trainer.trainerName} needs at least one Pokémon in the party before setting out.</p>
      </section>
    );
  }
  if (data.destinations.length === 0) {
    return (
      <section className="panel">
        <p className="eyebrow">Travel</p>
        <h2>Nowhere to go</h2>
        <p>There is no other game to travel to.</p>
      </section>
    );
  }

  // Somewhere new comes first; a region already conquered last.
  const options = [...data.destinations].sort(
    (a, b) => Number(a.conquered) - Number(b.conquered) || Number(a.game.region === data.fromRegion) - Number(b.game.region === data.fromRegion),
  );
  const destination = options.find((d) => d.game.id === picked) ?? options[0]!;
  const game = destination.game;
  const to = regionName(game.region);
  const way = PORTS[game.region];
  const coming = destination.party.filter((mon) => mon.canTravel);
  const staying = destination.party.filter((mon) => !mon.canTravel);

  const board = async () => {
    setBoarding(true);
    setRefused(undefined);
    try {
      onArrived(await api.board(game.id));
    } catch (e) {
      setRefused(e instanceof Error ? e.message : String(e));
    } finally {
      setBoarding(false);
    }
  };

  return (
    <>
      <section className="ticket rg" style={regionStyle(game.region)}>
        <div className="ticket-main">
          <p className="eyebrow">Boarding pass · {way.vessel}</p>
          <div className="route">
            <div>
              <p className="muted">From</p>
              <p className="place">{from}</p>
            </div>
            <span className="arrow" aria-hidden="true">
              →
            </span>
            <div>
              <p className="muted">To</p>
              <p className="place" style={{ color: 'var(--region-ink)' }}>
                {to}
              </p>
            </div>
          </div>
          <div>
            <label className="muted" htmlFor="dest">
              Destination
            </label>
            <br />
            <select id="dest" value={game.id} onChange={(e) => setPicked(e.target.value)} disabled={boarding}>
              {options.map((d) => (
                <option key={d.game.id} value={d.game.id}>
                  {regionName(d.game.region)} · {d.game.name}
                  {d.hasSave ? ' (been there)' : ''}
                  {d.conquered ? ' (title won)' : ''}
                </option>
              ))}
            </select>
          </div>
        </div>
        <div className="ticket-stub">
          <span className="eyebrow">Passenger</span>
          <b>{trainer.trainerName}</b>
          <span className="eyebrow">Party</span>
          <b>
            {coming.length} of {destination.party.length} Pokémon
          </b>
          <span className="px muted">
            No. {String(trainer.trainerId ?? 0).padStart(5, '0')}-{game.region.slice(0, 2).toUpperCase()}
          </span>
        </div>
      </section>

      <section className="panel">
        <div>
          <p className="eyebrow">Coming along</p>
          <h2>
            {coming.length} Pokémon will travel with {trainer.trainerName}
          </h2>
        </div>
        <div className="rows">
          {coming.map((mon, index) => {
            const lost = mon.movesLost.map((move) => names?.moves[move] ?? `move #${move}`);
            return (
              <div key={index} className="row">
                <MonSprite species={mon.species} size={48} fallback={mon.nickname} />
                <div className="grow">
                  <b>{mon.nickname}</b>
                  {lost.length > 0 && (
                    <div className="muted">
                      Will forget {listOf(lost)} while in {to}. {lost.length > 1 ? "Those moves don't" : "That move doesn't"} exist there.
                    </div>
                  )}
                </div>
                <span className={`pill ${lost.length > 0 ? 'warn' : 'ok'}`}>{lost.length > 0 ? 'Changes' : 'Ready'}</span>
              </div>
            );
          })}
        </div>
        {game.note && <div className="note">{game.note}</div>}
        <div className="note">
          {destination.hasSave
            ? `${trainer.trainerName} has been to ${game.name} before and picks up that save again.`
            : `This is ${trainer.trainerName}'s first time in ${game.name}: the trip starts a new save there.`}
        </div>
      </section>

      {staying.length > 0 && (
        <section className="panel">
          <div>
            <p className="eyebrow">Staying behind</p>
            <h2>These Pokémon can't make the trip</h2>
          </div>
          <div className="rows">
            {staying.map((mon, index) => (
              <div key={index} className="row">
                <MonSprite species={mon.species} size={48} fallback={mon.nickname} />
                <div className="grow">
                  <b>{mon.nickname}</b>
                </div>
                <span className="pill stay">Stays in storage</span>
              </div>
            ))}
          </div>
          <p className="muted">
            {staying.length > 1 ? "They haven't" : "It hasn't"} been discovered in {to} yet. {staying.length > 1 ? "They'll" : "It'll"} wait in
            storage and can rejoin on a later trip.
          </p>
        </section>
      )}

      {refused && <div className="error">{refused}</div>}
      <div className="actions rg" style={regionStyle(game.region)}>
        <button type="button" className="btn go" onClick={board} disabled={boarding || coming.length === 0}>
          {boarding ? 'Boarding…' : `Board the ${way.vessel}`}
        </button>
      </div>
    </>
  );
}