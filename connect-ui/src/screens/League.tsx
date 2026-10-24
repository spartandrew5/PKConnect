import { useState } from 'react';
import { api } from '../api/client';
import type { Catalog, Journey, LeagueRegion, Names, Trainer } from '../api/types';
import { useLoad } from '../api/useLoad';
import { portraitOf } from '../components/looks';
import { MonSprite } from '../components/Mon';
import { regionStyle, shortDate } from '../components/region';
import { Sprite } from '../components/Sprite';
import { Trophy } from 'lucide-react';

// A region's own champion, as the games draw them: each in the style of the game the trainer meets
// them in (Pokémon Showdown's trainer sprite collection). Anyone else shows their initial.
const SHOWDOWN_TRAINERS = 'https://play.pokemonshowdown.com/sprites/trainers';
const CHAMPION_SPRITES: Record<string, string> = {
  blue: 'blue-gen3',
  red: 'red',
  lance: 'lance',
  steven: 'steven-gen3',
  wallace: 'wallace-gen3',
  cynthia: 'cynthia-gen4',
  alder: 'alder',
  iris: 'iris',
};

// A player champion is pictured in the generation of the game they won the title in, so Kanto's
// champion from FireRed looks like a FireRed trainer and Johto's from HeartGold like a HeartGold one.
const GENERATION_OF_VERSION: Record<string, number> = {
  RD: 1, GN: 1, BU: 1, YW: 1,
  GD: 2, SI: 2, C: 2,
  R: 3, S: 3, E: 3, FR: 3, LG: 3,
  D: 4, P: 4, Pt: 4, HG: 4, SS: 4,
  B: 5, W: 5, B2: 5, W2: 5,
};
// Without that, the region's own Connect game's generation.
const GENERATION_OF_REGION: Record<string, number> = { kanto: 3, johto: 4, hoenn: 3, sinnoh: 4, unova: 5 };

function championGeneration(gameVersion: string | undefined, region: string): number | undefined {
  return (gameVersion && GENERATION_OF_VERSION[gameVersion]) || GENERATION_OF_REGION[region];
}
function ChampionPicture({ name }: { name: string }) {
  const [missing, setMissing] = useState(false);
  const file = CHAMPION_SPRITES[name.trim().toLowerCase()];
  if (!file || missing) return <>{name.slice(0, 1)}</>;
  return (
    <span className="spritebox champsprite">
      <img src={`${SHOWDOWN_TRAINERS}/${file}.png`} alt="" onError={() => setMissing(true)} />
    </span>
  );
}
function LeagueCard({
  entry,
  catalog,
  names,
  trainer,
  holder,
}: {
  entry: LeagueRegion;
  catalog: Catalog;
  names: Names | undefined;
  trainer: Trainer;
  /** The trainer of this player who holds the title, if one does. */
  holder: Trainer | undefined;
}) {
  const region = catalog.regions.find((r) => r.id === entry.region)!;
  const page = trainer.passport.find((p) => p.region === entry.region);
  const here = entry.region === trainer.currentRegion;
  const champion = entry.champion;
  const total = region.badges.length;

  let status: React.ReactNode;
  if (entry.holderTrainerId === trainer.id) {
    status = (
      <>
        <b>{trainer.trainerName} holds this title.</b> New challengers in {region.name} face this team.
      </>
    );
  } else if (here) {
    status = (
      <>
        <b>{trainer.trainerName}'s challenge:</b> {page?.badgeCount ?? 0} of {total} badges. The final battle is against{' '}
        {champion.championName}'s team.
        <div className="bar" style={{ marginTop: 8 }} aria-hidden="true">
          <i style={{ width: `${Math.round(((page?.badgeCount ?? 0) / total) * 100)}%` }} />
        </div>
      </>
    );
  } else if (!page?.visited) {
    status = `${trainer.trainerName} hasn't travelled to ${region.name} yet.`;
  } else {
    status = `${trainer.trainerName} has ${page.badgeCount} of ${total} badges here.`;
  }

  return (
    <article className={`lcard rg${here ? ' feat' : ''}`} style={regionStyle(entry.region)}>
      <div className="lband">
        <h3>{region.name}</h3>
        <span className="px">{here ? 'You are here · ' : ''}Pokémon League</span>
      </div>
      <div className="lbody">
        <div className="ltop">
          <div className="champ">
            <div className="portrait">
              {holder?.appearance?.character ? (
                <Sprite set={portraitOf(catalog, holder.appearance.character, holder.appearance, championGeneration(champion.gameVersion, region.id))} size={64} />
              ) : (
                <ChampionPicture name={champion.championName} />
              )}
            </div>
            <div>
              <span className="crown">Champion</span>
              <h4>{champion.championName}</h4>
              <span className="muted" style={{ fontSize: 14 }}>
                {entry.isOriginal
                  ? 'Original champion · undefeated'
                  : `${entry.holderTrainerId ? 'Your trainer' : 'A trainer'}${champion.beatenAt ? ` · since ${shortDate(champion.beatenAt)}` : ''}`}
              </span>
            </div>
          </div>
          <div className="team">
            {champion.team.map((mon, index) => {
              const name = names?.species[mon.species] ?? mon.nickname ?? `#${mon.species}`;
              return (
                <div key={index} className="mon">
                  <MonSprite species={mon.species} size={48} fallback={name} icon />
                  <span>
                    <b>{name}</b>
                    <small>Lv. {mon.level}</small>
                  </span>
                </div>
              );
            })}
          </div>
        </div>
        <div>
          <p className="eyebrow" style={{ marginBottom: 6 }}>
            Title history
          </p>
          <div className="lineage">
            {entry.pastHolders.map((holder, index) => (
              <span key={index} style={{ display: 'contents' }}>
                <span className="lchip">{holder}</span>
                <span className="muted" aria-hidden="true">
                  →
                </span>
              </span>
            ))}
            <span className="lchip cur">{champion.championName}</span>
          </div>
        </div>
        <div className="lstatus">{status}</div>
      </div>
    </article>
  );
}

export function League({
  trainer,
  journey,
  catalog,
  names,
}: {
  trainer: Trainer;
  journey: Journey;
  catalog: Catalog;
  names: Names | undefined;
}) {
  const { data, error } = useLoad(() => api.league(), [trainer.id]);
  if (error) return <div className="error">{error}</div>;
  if (!data) return <div className="note">Loading…</div>;

  // Who holds how many titles.
  const tally = new Map<string, number>();
  let originals = 0;
  for (const entry of data.regions) {
    if (entry.holderTrainerId) tally.set(entry.holderTrainerId, (tally.get(entry.holderTrainerId) ?? 0) + 1);
    else if (entry.isOriginal) originals++;
  }
  const holders = [...tally.entries()].sort((a, b) => b[1] - a[1]);

  const ordered = [...data.regions].sort((a, b) => Number(b.region === trainer.currentRegion) - Number(a.region === trainer.currentRegion));

  return (
    <>
      <section className="panel">
        <div className="dexhead">
          <div>
            <p className="eyebrow ilabel">
            <Trophy className="ico" size={14} aria-hidden="true" />
            League
          </p>
            <h2>Hall of champions</h2>
          </div>
          <div className="tally">
            {holders.map(([id, count]) => (
              <div key={id} className="tallyc">
                <b>{count}</b>
                <span>{journey.trainers.find((t) => t.id === id)?.trainerName ?? id}</span>
              </div>
            ))}
            {originals > 0 && (
              <div className="tallyc orig">
                <b>{originals}</b>
                <span>Original champions</span>
              </div>
            )}
          </div>
        </div>
      </section>
      <div className="lgrid">
        {ordered.map((entry) => (
          <LeagueCard
            key={entry.region}
            entry={entry}
            catalog={catalog}
            names={names}
            trainer={trainer}
            holder={journey.trainers.find((t) => t.id === entry.holderTrainerId)}
          />
        ))}
      </div>
    </>
  );
}