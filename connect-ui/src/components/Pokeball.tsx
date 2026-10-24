/** A Poké Ball, drawn like the line icons it sits beside (which come from lucide, where there is none). */
export function Pokeball({ size = 16, className }: { size?: number; className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="9.5" />
      <path d="M2.5 12h6.5M15 12h6.5" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}
