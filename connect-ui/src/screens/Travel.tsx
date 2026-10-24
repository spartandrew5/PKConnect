import { useState } from 'react';
import { api } from '../api/client';
import type { BoardingResult, Catalog, Names, RegionId, Trainer } from '../api/types';
import { useLoad } from '../api/useLoad';
import { MonSprite } from '../components/Mon';
import { gameIcon } from '../components/gameIcon';
import { MapPinned, Ship, Ticket, Users, UserX } from 'lucide-react';
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
  const [region, setRegion] = useState<RegionId>();
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
  // The first choice is made for the player (somewhere new); the pass is stamped with it from the start.
  const destination = options.find((d) => d.game.id === picked) ?? options[0];
  const game = destination?.game;
  const shownRegion = region ?? game?.region ?? options[0]!.game.region;
  const to = game ? regionName(game.region) : undefined;
  const way = game ? PORTS[game.region] : undefined;
  const coming = destination?.party.filter((mon) => mon.canTravel) ?? [];
  const staying = destination?.party.filter((mon) => !mon.canTravel) ?? [];
  // Every game of the region on show, the one the trainer is in included (it can't be picked).
  const regionGames = catalog.games.filter((g) => g.region === shownRegion);
  const board = async () => {
    setBoarding(true);
    setRefused(undefined);
    if (!game) return;
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
      <section className="ticket rg" style={regionStyle(game?.region ?? shownRegion)}>
        <div className="ticket-main">
          <p className="eyebrow ilabel">
            <Ticket className="ico" size={14} aria-hidden="true" />
            Boarding pass{way ? ` · ${way.vessel}` : ''}
          </p>
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
              <p className="place" style={{ color: to ? 'var(--region-ink)' : 'var(--muted)' }}>
                {to ?? '———'}
              </p>
            </div>
            {/* The chosen game, stamped onto the pass. Keyed so each new choice is stamped afresh. */}
            {game ? (
              <div key={game.id} className="stamp" aria-label={`Destination: ${game.name}`}>
                <img src={gameIcon(game.id)} alt="" />
                <span>{game.name}</span>
              </div>
            ) : (
              <div className="stamp blank">Pick a game below</div>
            )}
          </div>
        </div>
        <div className="ticket-stub">
          <span className="eyebrow">Passenger</span>
          <b>{trainer.trainerName}</b>
          <span className="eyebrow">Party</span>
          <b>{destination ? `${coming.length} of ${destination.party.length} Pokémon` : `${trainer.party.length} Pokémon`}</b>
          <span className="px muted">
            No. {String(trainer.trainerId ?? 0).padStart(5, '0')}-{(game?.region ?? '--').slice(0, 2).toUpperCase()}
          </span>
        </div>
      </section>

      <section className="panel">
        <div className="travelhead">
          <div>
            <p className="eyebrow ilabel">
              <MapPinned className="ico" size={14} aria-hidden="true" />
              Travel to
            </p>
            <h2>Where is {trainer.trainerName} going?</h2>
          </div>
          {/* The way on, at the head of the choice it acts on. */}
          <div className="rg" style={regionStyle(game?.region ?? shownRegion)}>
            <button type="button" className="btn go" onClick={board} disabled={boarding || !game || coming.length === 0}>
              {!boarding && <Ship className="ico" size={16} aria-hidden="true" />}
              {boarding ? 'Boarding…' : way ? `Board the ${way.vessel}` : 'Choose a destination'}
            </button>
          </div>
        </div>
        {refused && <div className="error">{refused}</div>}
        <div className="chooser">
          <div className="choosehalf">
            <p className="kind">Region</p>
            <div className="regioncards">
              {catalog.regions.map((r) => {
                const reachable = options.filter((d) => d.game.region === r.id);
                const here = r.id === data.fromRegion;
                return (
                  <button
                    key={r.id}
                    type="button"
                    className={`rcard rg${r.id === shownRegion ? ' on' : ''}`}
                    style={regionStyle(r.id)}
                    aria-pressed={r.id === shownRegion}
                    disabled={boarding || reachable.length === 0}
                    onClick={() => setRegion(r.id)}
                  >
                    <b>{r.name}</b>
                    <small>{reachable.length === 0 ? 'No other game' : here ? 'Here now' : reachable.every((d) => d.conquered) ? 'Title won' : `${reachable.length} game${reachable.length > 1 ? 's' : ''}`}</small>
                  </button>
                );
              })}
            </div>
          </div>
          <div className="choosehalf rg" style={regionStyle(shownRegion)}>
            <p className="kind">Game</p>
            <div className="gamecards">
              {regionGames.map((g) => {
                const option = options.find((d) => d.game.id === g.id);
                const current = g.id === data.fromGameId;
                return (
                  <button
                    key={g.id}
                    type="button"
                    className={`gcard${g.id === game?.id ? ' on' : ''}`}
                    aria-pressed={g.id === game?.id}
                    disabled={boarding || !option}
                    onClick={() => {
                      setPicked(g.id);
                      setRegion(g.region);
                    }}
                  >
                    <img src={gameIcon(g.id)} alt="" />
                    <b>{g.name}</b>
                    <small>{current ? 'Current' : !option ? 'Not available' : option.conquered ? 'Title won' : option.hasSave ? 'Been there' : ''}</small>
                  </button>
                );
              })}
            </div>
          </div>
        </div>
      </section>

      {destination && game && (
      <section className="panel">        <div>
          <p className="eyebrow ilabel">
            <Users className="ico" size={14} aria-hidden="true" />
            Coming along
          </p>
          <h2>
            {coming.length} Pokémon will travel with {trainer.trainerName}
          </h2>
        </div>
        <div className="rows">
          {coming.map((mon, index) => {
            const lost = mon.movesLost.map((move) => names?.moves[move] ?? `move #${move}`);
            return (
              <div key={index} className="row">
                <MonSprite species={mon.species} size={48} fallback={mon.nickname} icon />
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
      )}

      {staying.length > 0 && (
        <section className="panel">
          <div>
            <p className="eyebrow ilabel">
              <UserX className="ico" size={14} aria-hidden="true" />
              Staying behind
            </p>
            <h2>These Pokémon can't make the trip</h2>
          </div>
          <div className="rows">
            {staying.map((mon, index) => (
              <div key={index} className="row">
                <MonSprite species={mon.species} size={48} fallback={mon.nickname} icon />
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


    </>
  );
}