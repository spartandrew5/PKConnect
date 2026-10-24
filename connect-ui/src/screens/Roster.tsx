import type { Catalog, Trainer } from '../api/types';
import { MonSprite } from '../components/Mon';
import { gameStyle } from '../components/region';
import { SpriteGame } from '../components/gameSprites';

export function Roster({
  trainers,
  catalog,
  busy,
  onPick,
  onBack,
  onNew,
}: {
  trainers: Trainer[];
  catalog: Catalog;
  busy: boolean;
  onPick: (trainer: Trainer) => void;
  onBack: () => void;
  onNew: () => void;
}) {
  const active = trainers.find((trainer) => trainer.isActive);

  return (
    <section className="panel">
      <div className="dexhead">
        <div>
          <p className="eyebrow">Trainers</p>
          <h2>Who's travelling today?</h2>
        </div>
        {active && (
          <button type="button" className="btn" onClick={onBack}>
            Back to {active.trainerName}
          </button>
        )}
      </div>
      <div className="roster">
        {trainers.map((trainer) => {
          const game = catalog.games.find((g) => g.id === trainer.currentGameId);
          const region = catalog.regions.find((r) => r.id === trainer.currentRegion);
          const home = catalog.regions.find((r) => r.id === trainer.homeRegion);
          const badges = trainer.passport.reduce((total, page) => total + page.badgeCount, 0);
          const titles = trainer.passport.filter((page) => page.holdsTitle).length;
          const tint = trainer.currentRegion ?? trainer.homeRegion;
          const id = trainer.trainerId === undefined ? '-----' : String(trainer.trainerId).padStart(5, '0');

          return (
            <button
              key={trainer.id}
              type="button"
              className={`tmini rg${trainer.isActive ? ' active' : ''}`}
              style={gameStyle(trainer.currentGameId, tint)}
              disabled={busy}
              onClick={() => onPick(trainer)}
            >
              <span className="tmini-band">
                <span className="px">ID No. {id}</span>
                <span>{trainer.isActive ? 'Current' : ''}</span>
              </span>
              <span className="tmini-body">
                <h3>{trainer.trainerName}</h3>
                {/* Two trainers can carry the same name in their saves; the profile's name tells them apart. */}
                <span className="line">
                  {trainer.name !== trainer.trainerName ? `${trainer.name} · ` : ''}
                  {home ? `From ${home.name}` : 'No home region yet'}
                </span>
                <span className="line">
                  {region ? (
                    <>
                      <b>Now in {region.name}</b>
                      {game ? ` · ${game.name}` : ''}
                    </>
                  ) : (
                    'Not in a game yet'
                  )}
                </span>
                <span className="line">
                  {badges} badges · {titles} titles
                </span>
                {/* Each trainer's party as their own game draws it. */}
                <SpriteGame.Provider value={game}>
                <span className="minidots">
                  {trainer.party.length > 0 ? (
                    trainer.party.map((mon, index) => (
                      <MonSprite key={index} species={mon.species} size={34} fallback={mon.nickname} shiny={mon.isShiny} icon />
                    ))
                  ) : (
                    <span className="line">No Pokémon yet</span>
                  )}
                </span>
                </SpriteGame.Provider>
              </span>
            </button>
          );
        })}
        <button type="button" className="tmini new" onClick={onNew} disabled={busy}>
          <span className="plus">+</span>
          New trainer
        </button>
      </div>
    </section>
  );
}