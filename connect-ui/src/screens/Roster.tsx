import type { Catalog, Trainer } from '../api/types';

export function Roster({
  trainers,
  catalog,
  busy,
  onPick,
  onBack,
}: {
  trainers: Trainer[];
  catalog: Catalog;
  busy: boolean;
  onPick: (trainer: Trainer) => void;
  onBack: () => void;
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
              style={tint ? ({ '--region': `var(--${tint})` } as React.CSSProperties) : undefined}
              disabled={busy}
              onClick={() => onPick(trainer)}
            >
              <span className="tmini-band">
                <span className="px">ID No. {id}</span>
                <span>{trainer.isActive ? 'Current' : ''}</span>
              </span>
              <span className="tmini-body">
                <h3>{trainer.trainerName}</h3>
                <span className="line">{home ? `From ${home.name}` : 'No home region yet'}</span>
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
                <span className="minidots">
                  {trainer.party.length > 0 ? (
                    trainer.party.map((mon, index) => (
                      <span key={index} className="dot">
                        {mon.nickname.slice(0, 2)}
                      </span>
                    ))
                  ) : (
                    <span className="line">No Pokémon yet</span>
                  )}
                </span>
              </span>
            </button>
          );
        })}
      </div>
    </section>
  );
}