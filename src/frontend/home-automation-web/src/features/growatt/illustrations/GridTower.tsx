import { useSvgId } from './svgId'

/** A lattice transmission tower with two cross-arms and insulators. Muted when AC input supply is absent. */
export function GridTower({ active }: { active: boolean }) {
  const id = useSvgId('grid')
  const steel = active ? `url(#${id}-steel)` : '#8a949e'
  return <svg viewBox="0 0 64 64" width="100%" height="100%" aria-hidden="true" focusable="false" className="flow-illustration">
    <defs>
      <linearGradient id={`${id}-steel`} x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor="#5c8fc7" />
        <stop offset="1" stopColor="#2f5f96" />
      </linearGradient>
    </defs>
    <g stroke={steel} strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" fill="none">
      <path d="M32 6 L22 58 M32 6 L42 58" />
      <path d="M16 17 H48 M19 27 H45" />
      <path d="M27.6 30 L37.4 42 M36.4 30 L26.6 42 M25.4 44 L39.4 56 M38.6 44 L24.6 56" strokeWidth="1.4" />
      <path d="M16 58 H48" strokeWidth="2.6" />
    </g>
    <g fill={active ? '#9fd3ff' : '#c3cad1'}>
      {[17, 47].map(x => <rect key={`a${x}`} x={x - 1.4} y="17" width="2.8" height="5" rx="1.2" />)}
      {[20, 44].map(x => <rect key={`b${x}`} x={x - 1.4} y="27" width="2.8" height="5" rx="1.2" />)}
    </g>
    <path d="M2 23 Q9 26 16 22 M48 22 Q55 26 62 23" stroke={active ? '#7aa7d6' : '#b0b8c0'} strokeWidth="1" fill="none" />
  </svg>
}
