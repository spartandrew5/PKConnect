import type { PartyMon } from '../api/types';

/** A party member as a small chip: its name and level. */
export function Mon({ mon }: { mon: PartyMon }) {
  return (
    <div className="mon">
      <span className="dot">{mon.nickname.slice(0, 2)}</span>
      <span>
        <b>{mon.nickname}</b>
        <small>{mon.isEgg ? 'Egg' : `Lv. ${mon.level}`}</small>
      </span>
    </div>
  );
}