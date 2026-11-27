import { useSvgId } from './svgId'

/** A wall-mounted inverter cabinet with a small display, a status light and cooling fins. */
export function InverterBox({ active }: { active: boolean }) {
  const id = useSvgId('inverter')
  return <svg viewBox="0 0 64 64" width="100%" height="100%" aria-hidden="true" focusable="false" className="flow-illustration">
    <defs>
      <linearGradient id={`${id}-body`} x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stopColor="#f4f7fa" />
        <stop offset="1" stopColor="#c9d3dd" />
      </linearGradient>
      <linearGradient id={`${id}-screen`} x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#1f3a4f" />
        <stop offset="1" stopColor="#0f2333" />
      </linearGradient>
    </defs>
    <rect x="14" y="8" width="36" height="48" rx="5" fill={`url(#${id}-body)`} stroke="#7d8a97" strokeWidth="1.2" />
    <rect x="20" y="15" width="24" height="12" rx="2" fill={`url(#${id}-screen)`} />
    <path d="M23 23 L27 19 L30 22 L34 17 L41 23" stroke={active ? '#5ee0b8' : '#6f8392'} strokeWidth="1.4" fill="none"
      strokeLinecap="round" strokeLinejoin="round" />
    <circle cx="32" cy="33" r="2" fill={active ? '#19b37d' : '#9aa5b0'} />
    <g stroke="#a3afbb" strokeWidth="1.6" strokeLinecap="round">
      <line x1="21" y1="40" x2="43" y2="40" /><line x1="21" y1="44.5" x2="43" y2="44.5" /><line x1="21" y1="49" x2="43" y2="49" />
    </g>
    <rect x="24" y="56" width="4" height="4" rx="1" fill="#7d8a97" /><rect x="36" y="56" width="4" height="4" rx="1" fill="#7d8a97" />
  </svg>
}
