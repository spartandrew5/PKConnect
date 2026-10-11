import type {
  Appearance,
  BoardingResult,
  Catalog,
  JournalEntry,
  Journey,
  League,
  Names,
  PlaySettings,
  PlayStatus,
  TrainerProfiles,
  TravelPreview,
} from './types';

/** A request the backend refused, with the reason it gave. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

// The backend puts the reason for a refusal in a header (JSON-encoded), not in the body.
function reasonOf(response: Response): string {
  const header = response.headers.get('error-message');
  if (header) {
    try {
      return JSON.parse(header) as string;
    } catch {
      return header;
    }
  }
  return `The backend answered ${response.status}`;
}

async function request<T>(method: string, path: string, body?: FormData | object): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api/${path}`, {
      method,
      ...(body instanceof FormData
        ? { body }
        : body !== undefined
          ? { body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } }
          : {}),
    });
  } catch {
    throw new ApiError("The backend isn't answering. Is it running?", 0);
  }
  if (!response.ok) throw new ApiError(reasonOf(response), response.status);
  return (await response.json()) as T;
}

export const api = {
  catalog: () => request<Catalog>('GET', 'connect/catalog'),
  names: () => request<Names>('GET', 'connect/names'),
  journey: () => request<Journey>('GET', 'journey'),
  league: () => request<League>('GET', 'journey/league'),
  journal: (trainerId: string) => request<JournalEntry[]>('GET', `journey/journal/${encodeURIComponent(trainerId)}`),
  travelPreview: () => request<TravelPreview>('GET', 'journey/travel'),
  /** Takes the active trainer and their party to another game. Writes saves. */
  board: (gameId: string) => request<BoardingResult>('POST', `journey/travel/${encodeURIComponent(gameId)}`),
  /** Makes a trainer the one the app is about. */
  switchTrainer: (trainerId: string) => request<unknown>('PUT', `trainer-profiles/${encodeURIComponent(trainerId)}/switch`),
  /**
   * Brings a save into step with its trainer: badges, conquered regions. With afterPlay - when
   * the player comes back from the game or brings in a save - a League win makes them champion.
   */
  syncSave: (saveId: number, afterPlay = false) =>
    request<unknown>('POST', `trainer-profiles/sync-save/${saveId}?afterPlay=${afterPlay}`),
  createTrainer: (name: string, gameId?: string, homeRegion?: string) => {
    const query = new URLSearchParams({ name });
    if (gameId) query.set('pendingGameVersion', gameId);
    if (homeRegion) query.set('homeRegion', homeRegion);
    return request<TrainerProfiles>('POST', `trainer-profiles?${query}`);
  },
  renameTrainer: (trainerId: string, name: string) =>
    request<TrainerProfiles>('PUT', `trainer-profiles/${encodeURIComponent(trainerId)}?${new URLSearchParams({ name })}`),
  /** Adds a save file of the player's own to a trainer. */
  importSave: (trainerId: string, file: File) => {
    const form = new FormData();
    form.append('file', file, file.name);
    return request<TrainerProfiles>('POST', `trainer-profiles/${encodeURIComponent(trainerId)}/import-save`, form);
  },
  setAppearance: (trainerId: string, appearance: Appearance) =>
    request<Journey>('PUT', `journey/appearance/${encodeURIComponent(trainerId)}`, appearance),
  /** Starts the active trainer's game. Answers at once; playStatus says when they are back. */
  play: () => request<PlayStatus>('POST', 'play'),
  playStatus: () => request<PlayStatus>('GET', 'play'),
  playSettings: () => request<PlaySettings>('GET', 'play/settings'),
  setPlaySettings: (settings: PlaySettings) => request<PlaySettings>('PUT', 'play/settings', settings),
  portraitUrl: (characterId: string) => `/api/connect/characters/${encodeURIComponent(characterId)}/portrait`,
};