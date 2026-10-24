<!-- Rough draft. Replaces README.md when ready; items marked TODO still need filling in. -->

# Pokémon Connect

One trainer, every region. Pokémon Connect turns your Pokémon games into a single journey: a
trainer leaves one game with their party, arrives in another, takes on its gyms and League, and
can come home again. Whoever holds a region's title becomes the Champion that everyone else has to
beat there.

It runs alongside an emulator. You bring your own ROMs and saves; Connect looks after the trainers,
moves Pokémon between games, and patches each game as it starts so the world you walk into matches
your journey.

> **Status: in development.** It works end to end on the author's machine, but it is not packaged
> for release yet, and several things are only tested lightly. See [What isn't done](#what-isnt-done).

<!-- TODO: screenshots of the trainer card, Travel and League tabs -->

## What it does

- **Trainers.** Each trainer is a profile with its own saves, storage, badges and titles. A new
  trainer starts a new game; the name comes from that first save.
- **Trainer card and passport.** Where the trainer is, their party, and their badges in every region.
- **Travel.** Take the party to another game. Connect converts each Pokémon to the destination's
  format and tells you beforehand which ones can't make the trip and which moves would be lost.
- **Gyms that fit your team.** A trainer visiting a region faces gym leaders with teams scaled to
  the party they arrived with.
- **Regional Champions.** Beat a League and your Hall of Fame team becomes that region's Champion,
  with your name and, where the game allows, your look. The next trainer to challenge that League
  fights you.
- **Appearance.** Choose how the trainer looks in each generation. The chosen sprites are written
  into the game at launch.
- **PC and national storage.** Move Pokémon between the game's boxes and a storage shared by all
  of the trainer's games.
- **Pokédex, journal and League.** What you've seen and caught, a log of arrivals, badges and
  titles, and who holds each region.
- **Play.** Start the trainer's game from the app. When you close the emulator, Connect reads the
  save back in and updates badges and titles.

## Games

| Region | Games |
|---|---|
| Kanto | Yellow, FireRed, LeafGreen, FireRed and LeafGreen builds of pokeemerald-expansion |
| Johto | Crystal, HeartGold, SoulSilver |
| Hoenn | Emerald, the Emerald build of pokeemerald-expansion |
| Sinnoh | Platinum |
| Unova | Black, White, Black 2, White 2 |

The retail games must be the exact revision the patches were made for; Connect checks the ROM
before patching and refuses one it doesn't recognise. <!-- TODO: list the supported revisions -->

Some games are extended so that Pokémon from later generations can visit them. Platinum,
HeartGold and SoulSilver gain the Generation 5 species, and the pokeemerald-expansion builds
already carry later generations.

## What you need

- Your own ROMs of the games you want to play. **No ROMs are included, and none will be provided.**
- An emulator. mGBA (Game Boy, Game Boy Color, Game Boy Advance) and RetroArch with the melonDS
  core (Nintendo DS) are the ones tested so far.
- To build from source: the .NET 10 SDK and Node.js 22.
- An internet connection for Pokémon pictures in the game's own style; without one the app falls
  back to its built-in sprites.

## Running it

From source, as one program:

```
cd connect-ui
npm install
npm run build
cd ../PKVault.Backend
dotnet run
```

Then open http://localhost:5000. In Settings, point Connect at your emulators and at each game's ROM.

While working on the interface, run the backend and the dev server side by side:

```
cd PKVault.Backend && dotnet run        # API on port 5000
cd connect-ui && npm run dev            # interface on port 5174
```

With Docker:

```
docker build . --target monolith -t pokemon-connect
docker run -p 3000:3000 -v connect-data:/pkvault pokemon-connect
```

<!-- TODO: the Docker image can't start an emulator on your desktop; say what it is good for -->

A desktop build (`PKVault.Desktop`) also exists and runs on Windows. It isn't packaged yet.

## How a game gets patched

Nothing is written to your ROM. Each time a game starts, Connect builds a patch for that launch
and hands it to the emulator:

- a **base patch** that teaches the game about visiting trainers (built from the community
  decompilations for most games, and from direct edits of the retail data for Black/White and
  their sequels);
- **gym teams** drawn from a pool for each leader and levelled to the visiting party;
- the **region's Champion**: team, name and sprites;
- the **trainer's appearance**.

Your saves are kept per trainer. Connect backs them up before writing to them.

## What isn't done

- Not packaged: no installer or release builds yet, and the Linux and macOS desktop packages are
  known to be incomplete.
- Badges brought into Black, White, Black 2 or White 2 from another save show on the trainer card,
  but whether the gym leaders there treat themselves as beaten hasn't been confirmed.
- Black 2 and White 2 support is new and has had little play testing.
- The interface is still being designed and will change.
- Travel has no requirements yet; any trainer can go anywhere.
- No Android build.

## Project layout

| Folder | What it is |
|---|---|
| `PKConnect.Core` | Everything Connect adds: trainers, travel, launch patches, champions, sprites |
| `PKConnect.Core/connect-data` | Base patches, gym team pools, starting saves, trainer sprites |
| `connect-ui` | The interface (React, TypeScript, Vite) |
| `PKVault.Core`, `PKVault.Backend`, `PKVault.Desktop` | PKVault, which Connect is built on |
| `PKConnect.Core.Tests` | Tests; many run against real ROMs when you point them at yours |

## Credits

Pokémon Connect is a fork of **[PKVault](https://github.com/Chnapy/PKVault)** by Richard Haddad
(Chnapy), and could not exist without it or without:

- **[PKHeX](https://github.com/kwsch/PKHeX)** by Kaphotics and contributors, which reads and writes the saves;
- the **[pret](https://github.com/pret)** decompilation projects (pokered, pokecrystal, pokeemerald,
  pokefirered, pokeplatinum, pokeheartgold), which the base patches are built from;
- **[pokeemerald-expansion](https://github.com/rh-hideout/pokeemerald-expansion)** by RHH and contributors;
- **[hg-engine](https://github.com/BluRosie/hg-engine)** by BluRosie and contributors, for sprites
  of later-generation Pokémon in the Generation 4 style;
- **[Emerald Seaglass sprites](https://github.com/zitaoyu/seaglass-sprites)**, for later Pokémon
  in the Game Boy Color style;
- the **[PokeAPI sprite collection](https://github.com/PokeAPI/sprites)**;
- **[Universal Pokémon Randomizer FVX](https://github.com/upr-fvx/universal-pokemon-randomizer-fvx)**,
  whose custom player graphics collection many of the trainer sprites came through;
- **[PKForge](https://github.com/sofianeelhor/PKForge)** and **[Bulbapedia](https://bulbapedia.bulbagarden.net)**,
  which inspired interface styles.

**Trainer sprites.** The fan-made trainer sprite sets and the people who drew them are listed in
[`PKConnect.Core/connect-data/protagonists/CREDITS.md`](PKConnect.Core/connect-data/protagonists/CREDITS.md).
If a set is yours and the credit is wrong or missing, or you would like it removed, please open an issue.

<!-- TODO: credit for the Professor Oak battle sprite used in HeartGold/SoulSilver -->

## Licence and disclaimer

Released under the GNU General Public License v3, like PKVault.

This is a fan project. It is not affiliated with, endorsed by or sponsored by Nintendo, Game Freak,
Creatures or The Pokémon Company. Pokémon and all related names, characters and images are
trademarks and copyright of their respective owners. You need to own the games you play with it.
