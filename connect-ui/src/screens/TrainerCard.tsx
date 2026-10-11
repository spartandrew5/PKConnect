import type { Catalog, PassportRegion, Region, Trainer } from '../api/types';
import { Mon } from '../components/Mon';
import { Portrait } from '../components/Portrait';

function badgeIcon(region: Region, badge: string): string {
  return `/badge-icons/${region.id}-${badge.toLowerCase()}.png`;
}

function PassportPage({ region, page, here }: { region: Region; page: PassportRegion | undefined; here: boolean }) {
  const visited = page?.visited ?? false;
  const status = page?.holdsTitle ? (
    <span className="seal">Champion</span>
  ) : here ? (
    <span className="tag here">Here now</span>
  ) : (
    <span className="tag">{visited ? 'Visited' : 'Not visited'}</span>
  );

  return (
    <div className={`page rg${visited ? '' : ' none'}`} style={{ '--region': `var(--${region.id})` } as React.CSSProperties}>
      <div className="page-head">
        <h3>{region.name}</h3>
        {status}
        <span className="count">
          {page?.badgeCount ?? 0} of {region.badges.length} badges
        </span>
      </div>
      <div className="case">
        {region.badges.map((badge, index) => (
          <div key={badge} className={`bdg${page?.badges[index] ? ' on' : ''}`}>
            <img src={badgeIcon(region, badge)} alt="" />
            {badge}
          </div>
        ))}
      </div>
    </div>
  );
}

export function TrainerCard({
  trainer,
  catalog,
  playing,
  onPlay,
  onLook,
}: {
  trainer: Trainer;
  catalog: Catalog;
  /** The trainer's game is running right now. */
  playing: boolean;
  onPlay: () => void;
  onLook?: () => void;
}) {
  const game = catalog.games.find((g) => g.id === trainer.currentGameId);
  const region = catalog.regions.find((r) => r.id === trainer.currentRegion);
  const home = catalog.regions.find((r) => r.id === trainer.homeRegion);
  const badges = trainer.passport.reduce((total, page) => total + page.badgeCount, 0);
  const titles = trainer.passport.filter((page) => page.holdsTitle).length;
  const id = trainer.trainerId === undefined ? '-----' : String(trainer.trainerId).padStart(5, '0');

  return (
    <>
      <section className="tcard">
        <div className="tcard-band">
          <span className="px">TRAINER CARD</span>
          <span className="px">ID No. {id}</span>
        </div>
        <div className="tcard-body">
          <div className="who">
            <button type="button" className="portrait" onClick={onLook} disabled={!onLook} aria-label="Change appearance">
              {trainer.appearance?.character ? (
                <Portrait character={trainer.appearance.character} />
              ) : (
                trainer.trainerName.slice(0, 1)
              )}
            </button>
            <div className="who-text">
              <h1>{trainer.trainerName}</h1>
              <p className="muted">
                {home ? `From ${home.name}` : 'No home region yet'}
                {trainer.appearance?.character
                  ? ` · appears as ${catalog.characters.find((c) => c.id === trainer.appearance?.character)?.name ?? trainer.appearance.character}`
                  : ''}
              </p>
              {region && (
                <p className="here">
                  Now in {region.name}
                  {game ? ` · ${game.name}` : ''}
                </p>
              )}
            </div>
            <button type="button" className="btn go" disabled={!game || playing} onClick={onPlay}>
              {!game ? 'No game yet' : playing ? `Playing ${game.name}…` : trainer.saves.length === 0 ? `Start ${game.name}` : `Continue in ${game.name}`}
            </button>
          </div>

          {trainer.party.length > 0 ? (
            <div className="party">
              {trainer.party.map((mon, index) => (
                <Mon key={index} mon={mon} />
              ))}
            </div>
          ) : (
            <div className="note">
              No Pokémon yet. {trainer.trainerName} picks a first partner when the game starts.
              {trainer.saves.length === 0 && " Save in the game before closing it: that first save becomes this trainer."}
            </div>
          )}

          <div className="facts">
            <span>
              <b>{badges}</b> badges
            </span>
            <span>
              <b>{titles}</b> titles held
            </span>
            <span>
              <b>{trainer.hoursPlayed}</b> hours on the road
            </span>
          </div>
        </div>
      </section>

      <section className="panel">
        <div>
          <p className="eyebrow">Passport</p>
        </div>
        <div className="pages">
          {catalog.regions.map((r) => (
            <PassportPage
              key={r.id}
              region={r}
              page={trainer.passport.find((page) => page.region === r.id)}
              here={r.id === trainer.currentRegion}
            />
          ))}
        </div>
      </section>
    </>
  );
}