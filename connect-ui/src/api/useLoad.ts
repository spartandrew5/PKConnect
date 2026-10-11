import { useEffect, useState } from 'react';

/** What a screen is waiting for, got, or was refused. */
export type Loaded<T> = { data?: T; error?: string };

/** Loads something when the screen appears, and again whenever a dependency changes. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): Loaded<T> {
  const [state, setState] = useState<Loaded<T>>({});

  useEffect(() => {
    let current = true;
    setState({});
    load().then(
      (data) => current && setState({ data }),
      (error: unknown) => current && setState({ error: error instanceof Error ? error.message : String(error) }),
    );
    return () => {
      current = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return state;
}