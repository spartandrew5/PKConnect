// What the backend's Connect routes return. Kept by hand next to PKConnect.Core's DTOs.

export type RegionId = 'kanto' | 'johto' | 'hoenn' | 'sinnoh' | 'unova';

export interface Region {
  id: RegionId;
  name: string;
  /** The eight badges, in gym order. */
  badges: string[];
}

export interface Game {
  id: string;
  name: string;
  region: RegionId;
  regions: RegionId[];
  generation: number;
  maxSpecies: number;
  maxMove: number;
  playable: boolean;
  note?: string;
}

export interface Catalog {
  regions: Region[];
  games: Game[];
}

export interface PartyMon {
  species: number;
  nickname: string;
  level: number;
  isShiny: boolean;
  isEgg: boolean;
}

export interface PassportRegion {
  region: RegionId;
  visited: boolean;
  badges: boolean[];
  badgeCount: number;
  holdsTitle: boolean;
  conquered: boolean;
}

export interface TrainerSave {
  saveId: number;
  gameId?: string;
  region?: RegionId;
  playedSeconds: number;
  fileName: string;
}

export interface Appearance {
  character?: string;
  styles?: Record<string, string>;
}

export interface Trainer {
  id: string;
  name: string;
  isActive: boolean;
  trainerName: string;
  trainerId?: number;
  homeRegion?: RegionId;
  currentGameId?: string;
  currentRegion?: RegionId;
  currentSaveId?: number;
  hoursPlayed: number;
  party: PartyMon[];
  passport: PassportRegion[];
  saves: TrainerSave[];
  appearance?: Appearance;
}

/** English names by id: species[25] is "Pikachu". */
export interface Names {
  species: string[];
  moves: string[];
}

export interface JournalEntry {
  at: string;
  type: 'travel' | 'badge' | 'title';
  region?: RegionId;
  text: string;
}

export interface ChampionMon {
  species: number;
  level: number;
  nickname?: string;
  moves?: string[];
  heldItem?: string;
}

export interface Champion {
  championName: string;
  team: ChampionMon[];
  source: string;
  beatenAt?: string;
  gameVersion?: string;
}

export interface LeagueRegion {
  region: RegionId;
  champion: Champion;
  isOriginal: boolean;
  /** The trainer of this player who holds the title, if one does. */
  holderTrainerId?: string;
  pastHolders: string[];
}

export interface League {
  regions: LeagueRegion[];
}

export interface TravelMon {
  species: number;
  nickname: string;
  level: number;
  canTravel: boolean;
  /** Ids of the moves it knows that don't exist in the destination. */
  movesLost: number[];
}

export interface TravelDestination {
  game: Game;
  hasSave: boolean;
  conquered: boolean;
  party: TravelMon[];
}

export interface TravelPreview {
  fromGameId?: string;
  fromRegion?: RegionId;
  destinations: TravelDestination[];
}

export interface StayedBehind {
  species: number;
  nickname: string;
  level: number;
  reason: string;
}

export interface BoardingResult {
  saveId: number;
  gameId: string;
  region: RegionId;
  newSave: boolean;
  travelled: PartyMon[];
  stayed: StayedBehind[];
}

export interface Journey {
  activeTrainerId?: string;
  trainers: Trainer[];
}