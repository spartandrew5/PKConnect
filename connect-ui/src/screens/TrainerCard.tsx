import { useState } from 'react';
import { api } from '../api/client';
import type { Catalog, Names, PassportRegion, Region, Trainer } from '../api/types';
import { useLoad } from '../api/useLoad';
import { MonDetails } from '../components/MonDetails';
import { Pokeball } from '../components/Pokeball';
import { BookMarked, Clock, Crown, IdCard, Medal, Play } from 'lucide-react';
import { Mon } from '../components/Mon';
import { portraitOf } from '../components/looks';
import { Sprite } from '../components/Sprite';

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
  names,
  playing,
  onPlay,
  onLook,
}: {
  trainer: Trainer;
  catalog: Catalog;
  names: Names | undefined;
  /** The trainer's game is running right now. */
  playing: boolean;
  onPlay: () => void;
  onLook?: () => void;
}) {
  // The party in full, for the details window: the card itself only carries names and levels.
  const saveId = trainer.currentSaveId;
  const { data: stored } = useLoad(() => (saveId === undefined ? Promise.resolve([]) : api.saveMons(saveId)), [saveId, trainer.party.length]);
  const [open, setOpen] = useState<number>();
  const detailed = (index: number) => stored?.find((mon) => String(mon.boxId) === '-1' && mon.boxSlot === index);
  const shown = open === undefined ? undefined : detailed(open);

  const game = catalog.games.find((g) => g.id === trainer.currentGameId);
  const region = catalog.regions.find((r) => r.id === trainer.currentRegion);
  const home = catalog.regions.find((r) => r.id === trainer.homeRegion);
  const badges = trainer.passport.reduce((total, page) => total + page.badgeCount, 0);
  const titles = trainer.passport.filter((page) => page.holdsTitle).length;
  const id = trainer.trainerId === undefined ? '-----' : String(trainer.trainerId).padStart(5, '0');

  return (
    <>
      {shown && <MonDetails mon={shown} names={names} onClose={() => setOpen(undefined)} />}
      <section className="tcard">
        <div className="tcard-band">
          <span className="px ilabel">
            <Pokeball className="ico" size={16} />
            TRAINER CARD
          </span>
          <span className="px ilabel">
            <IdCard className="ico" size={16} aria-hidden="true" />
            ID No. {id}
          </span>
        </div>
        <div className="tcard-body">
          <div className="who">
            <div className="who-text">
              <h1>{trainer.trainerName}</h1>
              <p className="muted">
                {home ? `From ${home.name}` : 'No home region yet'}
                {trainer.appearance?.character
                  ? ` · appears as ${catalog.characters.find((c) => c.id === trainer.appearance?.character)?.name ?? trainer.appearance.character}`
                  : ''}
              </p>
              {/* Where the trainer is and the way back in, as one stack of equal pieces. */}
              <div className="whoactions">
                {region && (
                  <div className="locbox">
                    <span className="pin" aria-hidden="true">
                      <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                        <path d="M20 10c0 6-8 12-8 12s-8-6-8-12a8 8 0 0 1 16 0Z" />
                        <circle cx="12" cy="10" r="3" />
                      </svg>
                    </span>
                    <span className="loclabel">Region</span>
                    <span className="locwhere">
                      <b>{region.name}</b>
                      {game && <small>{game.name}</small>}
                    </span>
                  </div>
                )}
                <button type="button" className="btn go" disabled={!game || playing} onClick={onPlay}>
                  {game && !playing && <Play className="ico" size={16} aria-hidden="true" />}
                  {!game ? 'No game yet' : playing ? `Playing ${game.name}…` : trainer.saves.length === 0 ? `Start ${game.name}` : `Continue in ${game.name}`}
                </button>
              </div>
              {/* Beside the sprite, so its frame runs the full height of this column. */}
              <div className="facts">
                <span>
                  <Medal className="ico" size={16} aria-hidden="true" />
              <b>{badges}</b> badges
                </span>
                <span>
                  <Crown className="ico" size={16} aria-hidden="true" />
              <b>{titles}</b> titles held
                </span>
                <span>
                  <Clock className="ico" size={16} aria-hidden="true" />
              <b>{trainer.hoursPlayed}</b> hours on the road
                </span>
              </div>
            </div>
            {/* The trainer themselves, on the right like on the games' own cards. */}
            <button type="button" className="portrait" onClick={onLook} disabled={!onLook} aria-label="Change appearance">
              {trainer.appearance?.character ? (
                <Sprite set={portraitOf(catalog, trainer.appearance.character, trainer.appearance, game?.generation)} size={64} />
              ) : (
                trainer.trainerName.slice(0, 1)
              )}
              {onLook && <span>Change</span>}
            </button>
          </div>

          {trainer.party.length > 0 ? (
            <div className="party">
              {trainer.party.map((mon, index) => (
                <Mon key={index} mon={mon} onOpen={detailed(index) ? () => setOpen(index) : undefined} />
              ))}
            </div>
          ) : (
            <div className="note">
              No Pokémon yet. {trainer.trainerName} picks a first partner when the game starts.
              {trainer.saves.length === 0 && " Save in the game before closing it: that first save becomes this trainer."}
            </div>
          )}
        </div>
      </section>

      <section className="panel">
        <div>
          <p className="eyebrow ilabel">
            <BookMarked className="ico" size={14} aria-hidden="true" />
            Passport
          </p>
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