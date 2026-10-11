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

export interface Journey {
  activeTrainerId?: string;
  trainers: Trainer[];
}