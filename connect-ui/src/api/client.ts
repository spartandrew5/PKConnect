import type { BoardingResult, Catalog, JournalEntry, Journey, League, Names, TravelPreview } from './types';

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

async function request<T>(method: string, path: string): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api/${path}`, { method });
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
  /** Brings a save into step with its trainer: badges, conquered regions. */
  syncSave: (saveId: number) => request<unknown>('POST', `trainer-profiles/sync-save/${saveId}`),
};