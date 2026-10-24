/** A game's cartridge icon. The builds of pokeemerald-expansion wear the icon of the game they are built as. */
export function gameIcon(gameId: string): string {
  return `/game-icons/${gameId.replace(/expansion$/, '')}.png`;
}