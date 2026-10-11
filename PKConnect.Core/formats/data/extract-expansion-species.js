// One-time/dev-time extractor: parses the pokeemerald-expansion decomp repo's
// species data headers into a flat JSON table PKVault's backend can load.
// Run with: node extract-expansion-species.js <path-to-decomp-repo> <output.json>

const fs = require('fs');
const path = require('path');

const decompRoot = process.argv[2];
const outPath = process.argv[3];

if (!decompRoot || !outPath) {
  console.error('Usage: node extract-expansion-species.js <decompRoot> <outPath>');
  process.exit(1);
}

function readFile(rel) {
  return fs.readFileSync(path.join(decompRoot, rel), 'utf8');
}

// --- Parse simple "NAME = number," enum constant headers ---
function parseEnumConstants(text, prefixRegex) {
  const map = {};
  const re = new RegExp(`(${prefixRegex.source})\\s*=\\s*(\\d+)`, 'g');
  let m;
  while ((m = re.exec(text))) {
    map[m[1]] = parseInt(m[2], 10);
  }
  return map;
}

const speciesText = readFile('include/constants/species.h');
const speciesIds = parseEnumConstants(speciesText, /SPECIES_[A-Z0-9_]+/);

const abilitiesText = readFile('include/constants/abilities.h');
const abilityIds = parseEnumConstants(abilitiesText, /ABILITY_[A-Z0-9_]+/);

// Type/EggGroup/GrowthRate enums have no explicit "= N" for every member (sequential),
// so parse them positionally instead.
function parseSequentialEnum(text, enumName) {
  const enumMatch = text.match(new RegExp(`enum[^{]*${enumName}[^{]*\\{([\\s\\S]*?)\\}`));
  if (!enumMatch) throw new Error(`enum ${enumName} not found`);
  // Strip comments first: a "// Gen 1" header line would otherwise merge with the next entry.
  const body = enumMatch[1].replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
  const names = body
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0 && !s.startsWith('#'));
  const map = {};
  let next = 0;
  for (const raw of names) {
    const eqIdx = raw.indexOf('=');
    if (eqIdx >= 0) {
      const name = raw.slice(0, eqIdx).trim();
      const valueExpr = raw.slice(eqIdx + 1).trim();
      const asNum = parseInt(valueExpr, 10);
      if (!Number.isNaN(asNum)) {
        next = asNum;
        map[name] = next;
      } else if (map[valueExpr] !== undefined) {
        // Aliases like MOVE_SKETCH = MOVES_COUNT_GEN1: counting continues from the alias.
        next = map[valueExpr];
        map[name] = next;
      }
    } else {
      map[raw] = next;
    }
    next++;
  }
  return map;
}

const pokemonConstantsText = readFile('include/constants/pokemon.h');
const typeIds = parseSequentialEnum(pokemonConstantsText, 'Type');
const eggGroupIds = parseSequentialEnum(pokemonConstantsText, 'EggGroup');
const growthRateIds = parseSequentialEnum(pokemonConstantsText, 'GrowthRate');

// National dex order enum (constants/pokedex.h) - also sequential with some explicit resets.
const pokedexConstantsText = readFile('include/constants/pokedex.h');
const natDexOrderIds = parseSequentialEnum(pokedexConstantsText, 'NationalDexOrder');

function decompTypeToPKHeX(decompTypeName) {
  const id = typeIds[decompTypeName];
  if (id === undefined) return 0;
  if (id === 0) return 0; // TYPE_NONE -> Normal fallback
  return id <= 9 ? id - 1 : id - 2; // skip TYPE_MYSTERY gap at 10
}

// --- Parse per-species struct literals out of species_info/gen_*_families.h ---
const genFiles = [1, 2, 3, 4, 5, 6, 7, 8, 9].map(
  (g) => `src/data/pokemon/species_info/gen_${g}_families.h`,
);

function findMatchingBrace(text, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < text.length; i++) {
    if (text[i] === '{') depth++;
    else if (text[i] === '}') {
      depth--;
      if (depth === 0) return i;
    }
  }
  return -1;
}

function extractField(block, fieldName) {
  const re = new RegExp(`\\.${fieldName}\\s*=\\s*([^,\\n]+(?:,[^,\\n]*)*?)(?:,\\s*\\n|,\\s*\\.|\\n\\s*\\.)`);
  // Simpler: capture up to the next ".xxx =" at start of a line, or a top-level comma before newline.
  const idx = block.indexOf(`.${fieldName}`);
  if (idx === -1) return null;
  const eqIdx = block.indexOf('=', idx);
  // find end: next occurrence of "\n        ." at depth 0, tracking parens/braces
  let depth = 0;
  let i = eqIdx + 1;
  for (; i < block.length; i++) {
    const c = block[i];
    if (c === '(' || c === '{') depth++;
    else if (c === ')' || c === '}') depth--;
    else if (c === ',' && depth === 0) break;
  }
  return block.slice(eqIdx + 1, i).trim();
}

function parseTypesField(value) {
  // e.g. "MON_TYPES(TYPE_GRASS, TYPE_POISON)" or "MON_TYPES(TYPE_NORMAL)"
  const m = value.match(/MON_TYPES\(([^)]*)\)/);
  if (!m) return [0, 0];
  const parts = m[1].split(',').map((s) => s.trim());
  const t1 = decompTypeToPKHeX(parts[0]);
  const t2 = parts.length > 1 ? decompTypeToPKHeX(parts[1]) : t1;
  return [t1, t2];
}

function parseEggGroupsField(value) {
  const m = value.match(/MON_EGG_GROUPS\(([^)]*)\)/);
  if (!m) return [0, 0];
  const parts = m[1].split(',').map((s) => s.trim());
  const g1 = eggGroupIds[parts[0]] ?? 0;
  const g2 = parts.length > 1 ? eggGroupIds[parts[1]] ?? g1 : g1;
  return [g1, g2];
}

function parseAbilitiesField(value) {
  // e.g. "{ ABILITY_OVERGROW, ABILITY_NONE, ABILITY_CHLOROPHYLL }"
  const m = value.match(/\{([^}]*)\}/);
  if (!m) return [0, 0, 0];
  const parts = m[1].split(',').map((s) => s.trim());
  return [0, 1, 2].map((i) => (parts[i] ? abilityIds[parts[i]] ?? 0 : 0));
}

function parseGenderRatio(value) {
  const pf = value.match(/PERCENT_FEMALE\(([\d.]+)\)/);
  if (pf) return Math.min(254, Math.floor((parseFloat(pf[1]) * 255) / 100));
  if (value.includes('MON_GENDERLESS')) return 0xff;
  if (value.includes('MON_FEMALE')) return 0xfe;
  if (value.includes('MON_MALE')) return 0x00;
  return 0xff;
}

function parseIntField(value, fallback = 0) {
  const n = parseInt(value, 10);
  return Number.isNaN(n) ? fallback : n;
}

const results = {};
const learnsetRefs = {};
let parsedCount = 0;
let skippedCount = 0;

for (const relPath of genFiles) {
  const text = readFile(relPath);
  const entryRe = /\[(SPECIES_[A-Z0-9_]+)\]\s*=\s*\{/g;
  let m;
  while ((m = entryRe.exec(text))) {
    const speciesName = m[1];
    const openBraceIdx = m.index + m[0].length - 1;
    const closeBraceIdx = findMatchingBrace(text, openBraceIdx);
    if (closeBraceIdx === -1) continue;
    const block = text.slice(openBraceIdx + 1, closeBraceIdx);

    const speciesId = speciesIds[speciesName];
    if (speciesId === undefined) {
      skippedCount++;
      continue;
    }

    const baseHP = extractField(block, 'baseHP');
    if (baseHP === null) {
      // Not a full species entry (e.g. a form referencing shared base data) - skip.
      skippedCount++;
      continue;
    }

    const typesRaw = extractField(block, 'types');
    const eggGroupsRaw = extractField(block, 'eggGroups');
    const abilitiesRaw = extractField(block, 'abilities');
    const genderRaw = extractField(block, 'genderRatio');
    const natDexRaw = extractField(block, 'natDexNum');

    const [type1, type2] = typesRaw ? parseTypesField(typesRaw) : [0, 0];
    const [egg1, egg2] = eggGroupsRaw ? parseEggGroupsField(eggGroupsRaw) : [0, 0];
    const [ability1, ability2, abilityHidden] = abilitiesRaw
      ? parseAbilitiesField(abilitiesRaw)
      : [0, 0, 0];

    results[speciesId] = {
      name: speciesName,
      baseHP: parseIntField(extractField(block, 'baseHP')),
      baseAtk: parseIntField(extractField(block, 'baseAttack')),
      baseDef: parseIntField(extractField(block, 'baseDefense')),
      baseSpe: parseIntField(extractField(block, 'baseSpeed')),
      baseSpA: parseIntField(extractField(block, 'baseSpAttack')),
      baseSpD: parseIntField(extractField(block, 'baseSpDefense')),
      type1,
      type2,
      catchRate: parseIntField(extractField(block, 'catchRate')),
      baseExp: parseIntField(extractField(block, 'expYield')),
      genderRatio: genderRaw ? parseGenderRatio(genderRaw) : 0xff,
      eggCycles: parseIntField(extractField(block, 'eggCycles')),
      growthRate: growthRateIds[extractField(block, 'growthRate') || ''] ?? 0,
      eggGroup1: egg1,
      eggGroup2: egg2,
      ability1,
      ability2,
      abilityHidden,
      natDexNum: natDexRaw ? (natDexOrderIds[natDexRaw] ?? 0) : 0,
    };

    const evolutionsRaw = extractField(block, 'evolutions');
    learnsetRefs[speciesId] = {
      levelUp: extractField(block, 'levelUpLearnset'),
      egg: extractField(block, 'eggMoveLearnset'),
      evolvesInto: evolutionsRaw
        ? [...evolutionsRaw.matchAll(/SPECIES_[A-Z0-9_]+/g)].map((x) => speciesIds[x[0]]).filter((x) => x !== undefined)
        : [],
    };
    parsedCount++;
  }
}

// --- Learnable moves (for PKVault's move editor) ---
// Union over the species and its pre-evolutions of: level-up moves, egg moves, and the teachable
// candidates in all_learnables.json, plus universal moves. The build filters teachables further to the
// TMs/tutors present in the ROM (teachable_learnsets.h, generated by make) - this is the superset.
const moveIds = parseSequentialEnum(readFile('include/constants/moves.h'), 'Move');
const moveListToIds = (body) =>
  [...body.matchAll(/MOVE_[A-Z0-9_]+/g)].map((x) => moveIds[x[0]]).filter((id) => id > 0);

const levelUpSets = {};
for (let g = 1; g <= 9; g++) {
  const text = readFile(`src/data/pokemon/level_up_learnsets/gen_${g}.h`);
  for (const lm of text.matchAll(/static const struct LevelUpMove (\w+)\[\]\s*=\s*\{([\s\S]*?)\};/g)) {
    levelUpSets[lm[1]] = moveListToIds(lm[2]);
  }
}
const eggSets = {};
for (const em of readFile('src/data/pokemon/egg_moves.h').matchAll(/static const u16 (\w+)\[\]\s*=\s*\{([\s\S]*?)\};/g)) {
  eggSets[em[1]] = moveListToIds(em[2]);
}
const allLearnables = JSON.parse(readFile('src/data/pokemon/all_learnables.json'));
const universalMoves = JSON.parse(readFile('src/data/pokemon/special_movesets.json')).universalMoves ?? [];

const preEvolution = {};
for (const [id, refs] of Object.entries(learnsetRefs)) {
  for (const child of refs.evolvesInto) {
    if (preEvolution[child] === undefined && String(child) !== id) preEvolution[child] = Number(id);
  }
}

for (const id of Object.keys(results)) {
  const moves = new Set(moveListToIds(universalMoves.join(',')));
  let current = Number(id);
  for (let depth = 0; current !== undefined && depth < 5; depth++) {
    const refs = learnsetRefs[current];
    if (refs) {
      for (const m of levelUpSets[refs.levelUp] ?? []) moves.add(m);
      for (const m of eggSets[refs.egg] ?? []) moves.add(m);
    }
    const key = results[current]?.name.replace(/^SPECIES_/, '');
    for (const m of moveListToIds((allLearnables[key] ?? []).join(','))) moves.add(m);
    current = preEvolution[current];
  }
  results[id].learnableMoves = [...moves].sort((a, b) => a - b);
}

fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, JSON.stringify(results));

// Held items: expansion item id -> constant name (e.g. 472 -> "LEFTOVERS"). The backend matches
// these by normalized name against PKHeX's modern item list, since expansion ids are their own order.
const itemIds = parseEnumConstants(readFile('include/constants/items.h'), /ITEM_[A-Z0-9_]+/);
const itemNames = {};
for (const [name, id] of Object.entries(itemIds)) {
  if (id > 0 && itemNames[id] === undefined) itemNames[id] = name.replace(/^ITEM_/, '');
}
const itemsOutPath = path.join(path.dirname(outPath), 'expansion_items.json');
fs.writeFileSync(itemsOutPath, JSON.stringify(itemNames));
console.log(`Wrote ${Object.keys(itemNames).length} items to ${itemsOutPath}`);

console.log(`Parsed ${parsedCount} species entries, skipped ${skippedCount} (forms/shared refs).`);
console.log(`Wrote ${outPath}`);

// Sanity spot-checks
for (const [name, id] of [
  ['SPECIES_BULBASAUR', speciesIds.SPECIES_BULBASAUR],
  ['SPECIES_RILLABOOM', speciesIds.SPECIES_RILLABOOM],
]) {
  if (id !== undefined && results[id]) {
    console.log(name, id, JSON.stringify(results[id]));
  } else {
    console.log(name, id, 'NOT FOUND IN RESULTS');
  }
}
