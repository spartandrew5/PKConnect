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

export type SpriteKind = 'front' | 'back' | 'overworld';

/** One sprite set of a character: its battle front, back or overworld sheet, as drawn for one game. */
export interface SpriteSet {
  /** "<character>/gen3/front/emerald" */
  id: string;
  generation: number;
  kind: SpriteKind;
  variant: string;
  title: string;
  /** The part of the set's image to show: x, y, width, height. */
  frame?: [number, number, number, number];
}

/** A character a trainer can look like. */
export interface Character {
  id: string;
  name: string;
  order: number;
  /** The generations it has sprites for; elsewhere the game's own protagonist is kept. */
  generations: string[];
  sets: SpriteSet[];
}

/** The sets chosen for one generation, by id. The back follows the front. */
export interface SpriteChoice {
  front?: string;
  overworld?: string;
}

export interface Catalog {
  regions: Region[];
  games: Game[];
  characters: Character[];
}

export interface Emulator {
  path: string;
  /** For a frontend like RetroArch: the core to load the game with. */
  corePath?: string;
}

export interface PlaySettings {
  /** By platform: "gba" plays the Game Boy and GBA games, "nds" the DS ones. */
  emulators: Record<string, Emulator>;
  /** The unmodified ROM of each game, by game id. */
  romPaths: Record<string, string>;
}

export interface PlayStatus {
  state: 'idle' | 'running' | 'finished' | 'failed';
  trainerId?: string;
  gameId?: string;
  startedAt?: string;
  message?: string;
  crownedRegion?: RegionId;
  newSave: boolean;
}

export interface TrainerProfiles {
  activeProfileId?: string;
  createdProfileId?: string;
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
  /** The sets picked for a generation, by its number ("3"). Left out = the character's first set. */
  sprites?: Record<string, SpriteChoice>;
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

/** Where a species' picture is: one of the sheets under /imgs/sheets, and the rectangle on it. */
export interface SpeciesSprite {
  sheet: string;
  x: number;
  y: number;
  width: number;
  height: number;
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

export interface DexEntry {
  /** Its number in the Pokédex shown. */
  number: number;
  /** Its National Dex number. */
  species: number;
  seen: boolean;
  caught: boolean;
}

export interface TrainerDex {
  scope: 'regional' | 'national';
  region?: RegionId;
  gameId?: string;
  seen: number;
  caught: number;
  entries: DexEntry[];
}

// PKVault's own storage routes; only what the PC screen reads of them.

export interface StorageBox {
  id: string;
  /** 0 a box, -1 the party; the rest (daycare, item pockets) isn't shown. */
  type: number;
  name: string;
  slotCount: number;
  order: number;
}

/** A Pokémon in a save (party or box) or in main storage. */
export interface StoredMon {
  id: string;
  species: number;
  level: number;
  nickname: string;
  /** A number in a save, a string in main storage. */
  boxId: number | string;
  boxSlot: number;
  isEgg: boolean;
  isShiny: boolean;
  /** Main storage keeps one Pokémon as several variants in one slot; this is the one to show. */
  isMain?: boolean;
  attachedSaveId?: number;
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