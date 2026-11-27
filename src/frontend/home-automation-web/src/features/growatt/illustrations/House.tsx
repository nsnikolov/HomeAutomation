import { useSvgId } from './svgId'

/** A small house with a pitched roof and lit windows while the inverters supply it. */
export function House({ active }: { active: boolean }) {
  const id = useSvgId('house')
  return <svg viewBox="0 0 64 64" width="100%" height="100%" aria-hidden="true" focusable="false" className="flow-illustration">
    <defs>
      <linearGradient id={`${id}-roof`} x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#d9694a" />
        <stop offset="1" stopColor="#a8462e" />
      </linearGradient>
      <linearGradient id={`${id}-wall`} x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stopColor="#fbf6ee" />
        <stop offset="1" stopColor="#e2d6c4" />
      </linearGradient>
    </defs>
    <rect x="42" y="12" width="6" height="12" rx="1" fill="#8d6e5b" />
    <rect x="14" y="30" width="36" height="26" rx="2" fill={`url(#${id}-wall)`} stroke="#9c8a75" strokeWidth="1.1" />
    <path d="M8 32 L32 12 L56 32 Z" fill={`url(#${id}-roof)`} stroke="#82351f" strokeWidth="1.2" strokeLinejoin="round" />
    <rect x="28" y="40" width="9" height="16" rx="1.5" fill="#7a5843" />
    <g fill={active ? '#ffd166' : '#cfd6dd'} stroke="#9c8a75" strokeWidth="0.8">
      <rect x="18" y="37" width="7" height="7" rx="1" /><rect x="40" y="37" width="7" height="7" rx="1" />
    </g>
    <rect x="4" y="56" width="56" height="3" rx="1.5" fill="#8fb07a" />
  </svg>
}
