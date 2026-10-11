import { useEffect, useState } from 'react';
import { api } from '../api/client';
import type { Catalog, PlaySettings } from '../api/types';

const PLATFORMS: { id: string; name: string; hint: string; core: boolean }[] = [
  { id: 'gba', name: 'Game Boy and GBA games', hint: 'An emulator that loads a patch beside the ROM by itself, such as mGBA.', core: false },
  { id: 'nds', name: 'DS games', hint: 'RetroArch with a DS core. Standalone DS emulators do not load the patch.', core: true },
];

/** Where the emulators and the games are on this machine. Paths are typed: a browser can't see them. */
export function Settings({ catalog, onDone }: { catalog: Catalog; onDone: () => void }) {
  const [settings, setSettings] = useState<PlaySettings>();
  const [problem, setProblem] = useState<string>();
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.playSettings().then(setSettings, (e) => setProblem(e instanceof Error ? e.message : String(e)));
  }, []);

  if (!settings) return <div className={problem ? 'error' : 'note'}>{problem ?? 'Loading…'}</div>;

  const change = (next: PlaySettings) => {
    setSettings(next);
    setSaved(false);
  };
  const setEmulator = (platform: string, field: 'path' | 'corePath', value: string) =>
    change({
      ...settings,
      emulators: { ...settings.emulators, [platform]: { ...(settings.emulators[platform] ?? { path: '' }), [field]: value } },
    });
  const setRom = (gameId: string, value: string) => change({ ...settings, romPaths: { ...settings.romPaths, [gameId]: value } });

  const save = async () => {
    setBusy(true);
    setProblem(undefined);
    try {
      setSettings(await api.setPlaySettings(settings));
      setSaved(true);
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <section className="panel">
        <div className="dexhead">
          <div>
            <p className="eyebrow">Settings</p>
            <h2>Emulators</h2>
          </div>
          <button type="button" className="btn" onClick={onDone}>
            Done
          </button>
        </div>
        {PLATFORMS.map((platform) => (
          <div key={platform.id} className="group">
            <div className="field">
              <label htmlFor={`emu-${platform.id}`}>{platform.name}</label>
              <input
                id={`emu-${platform.id}`}
                type="text"
                spellCheck={false}
                placeholder="Full path to the emulator's program"
                value={settings.emulators[platform.id]?.path ?? ''}
                onChange={(e) => setEmulator(platform.id, 'path', e.target.value)}
              />
              <small>{platform.hint}</small>
            </div>
            {platform.core && (
              <div className="field">
                <label htmlFor={`core-${platform.id}`}>Core</label>
                <input
                  id={`core-${platform.id}`}
                  type="text"
                  spellCheck={false}
                  placeholder="Full path to the core, e.g. melonds_libretro.dll"
                  value={settings.emulators[platform.id]?.corePath ?? ''}
                  onChange={(e) => setEmulator(platform.id, 'corePath', e.target.value)}
                />
              </div>
            )}
          </div>
        ))}
      </section>

      <section className="panel">
        <div>
          <p className="eyebrow">Settings</p>
          <h2>Games</h2>
          <p className="muted" style={{ fontSize: 14 }}>
            The unmodified ROM of each game you have. It is never changed: a patch is written beside it at every launch, and the save being
            played sits beside it under the ROM's name.
          </p>
        </div>
        <div className="form">
          {catalog.games.map((game) => (
            <div key={game.id} className="field">
              <label htmlFor={`rom-${game.id}`}>{game.name}</label>
              <input
                id={`rom-${game.id}`}
                type="text"
                spellCheck={false}
                placeholder="Full path to the ROM"
                value={settings.romPaths[game.id] ?? ''}
                onChange={(e) => setRom(game.id, e.target.value)}
              />
            </div>
          ))}
        </div>
        {problem && <p className="err">{problem}</p>}
        <div className="actions">
          {saved && <span className="pill ok">Saved</span>}
          <button type="button" className="btn go" onClick={save} disabled={busy}>
            {busy ? 'Saving…' : 'Save settings'}
          </button>
        </div>
      </section>
    </>
  );
}