// Style lab: trial looks for the app, layered over styles.css without touching it.
// Loaded by the dev server only. To drop the experiment, delete this folder and
// the one line in main.tsx that loads it.
import './classic.css';
import './pkforge.css';
import './bulbapedia.css';
import './home.css';
import './soft.css';
import './handheld.css';
import './width.css';

const STYLES: [id: string, label: string][] = [
  ['', 'Current'],
  ['classic', 'Classic (old app)'],
  ['pkforge', 'PKForge'],
  ['bulbapedia', 'Bulbapedia'],
  ['home', 'Pokémon HOME'],
  ['soft', 'Soft'],
  ['handheld', 'Handheld'],
];
const MODES: [id: string, label: string][] = [
  ['', 'Auto'],
  ['light', 'Light'],
  ['dark', 'Dark'],
];

const WIDTHS: [id: string, label: string][] = [
  ['', 'Normal width'],
  ['frame', 'Fixed frame'],
  ['tabs', 'Wide for Pokédex + PC'],
  ['wide', 'Wide'],
  ['full', 'Full width'],
];

const STYLE_KEY = 'connect-style-lab';
const WIDTH_KEY = 'connect-style-lab-width';
const MODE_KEY = 'connect-style-lab-mode';

function recall(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function remember(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    // The pick just won't outlive the page.
  }
}

function apply(attribute: string, value: string) {
  if (value) document.documentElement.setAttribute(attribute, value);
  else document.documentElement.removeAttribute(attribute);
}

function picker(options: [string, string][], value: string, onPick: (value: string) => void): HTMLSelectElement {
  const select = document.createElement('select');
  for (const [id, label] of options) select.add(new Option(label, id, false, id === value));
  select.addEventListener('change', () => onPick(select.value));
  select.style.cssText = 'font:inherit;padding:2px 6px;border-radius:6px;background:#fff;color:#111;border:1px solid #999';
  return select;
}

// ?style=<id>&mode=<light|dark>&width=<wide|full> picks for one page load (handy for comparing in two tabs).
const query = new URLSearchParams(location.search);
const style = query.get('style') ?? recall(STYLE_KEY) ?? '';
const mode = query.get('mode') ?? recall(MODE_KEY) ?? '';
apply('data-style', style);
apply('data-theme', mode);
const width = query.get('width') ?? recall(WIDTH_KEY) ?? '';
apply('data-width', width);

const bar = document.createElement('div');
bar.style.cssText =
  'position:fixed;left:12px;bottom:12px;z-index:9999;display:flex;gap:6px;align-items:center;padding:6px 8px;' +
  'border-radius:8px;background:#111c;color:#fff;font:12px system-ui,sans-serif;backdrop-filter:blur(6px)';
bar.append(
  'Style',
  picker(STYLES, style, (value) => {
    apply('data-style', value);
    remember(STYLE_KEY, value);
  }),
  picker(MODES, mode, (value) => {
    apply('data-theme', value);
    remember(MODE_KEY, value);
  }),
  picker(WIDTHS, width, (value) => {
    apply('data-width', value);
    remember(WIDTH_KEY, value);
  }),
);
document.body.append(bar);
