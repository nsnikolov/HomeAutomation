import { useSvgId } from './svgId'

/** A tilted PV panel on a stand with a small sun. Muted grey, without the sun, when there is no PV production. */
export function SolarPanel({ active }: { active: boolean }) {
  const id = useSvgId('solar')
  return <svg viewBox="0 0 64 64" width="100%" height="100%" aria-hidden="true" focusable="false" className="flow-illustration">
    <defs>
      <linearGradient id={`${id}-cells`} x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stopColor={active ? '#3b82c4' : '#9aa6b2'} />
        <stop offset="1" stopColor={active ? '#1d4f86' : '#6b7682'} />
      </linearGradient>
      <radialGradient id={`${id}-sun`} cx="0.5" cy="0.5" r="0.5">
        <stop offset="0" stopColor="#ffe08a" />
        <stop offset="1" stopColor="#f5a524" />
      </radialGradient>
    </defs>
    {active && <g>
      <circle cx="49" cy="14" r="7" fill={`url(#${id}-sun)`} />
      {[0, 45, 90, 135, 180, 225, 270, 315].map(angle =>
        <line key={angle} x1="49" y1="3.5" x2="49" y2="1" stroke="#f5a524" strokeWidth="1.6" strokeLinecap="round"
          transform={`rotate(${angle} 49 14)`} />)}
    </g>}
    <path d="M8 40 L20 20 L52 20 L44 40 Z" fill={`url(#${id}-cells)`} stroke={active ? '#163f6b' : '#59636e'} strokeWidth="1.2"
      strokeLinejoin="round" />
    <g stroke={active ? '#9cc7ee' : '#c3cad1'} strokeWidth="0.8" opacity="0.85">
      <line x1="18.7" y1="20" x2="10.7" y2="40" /><line x1="29.3" y1="20" x2="21.3" y2="40" />
      <line x1="40" y1="20" x2="32" y2="40" /><line x1="50.7" y1="20" x2="42.7" y2="40" />
      <line x1="14" y1="30" x2="48" y2="30" />
    </g>
    <path d="M20 21 L26 21 L18 38 L12 38 Z" fill="#fff" opacity={active ? 0.18 : 0.1} />
    <rect x="25" y="40" width="4" height="13" rx="1" fill="#7b8794" />
    <rect x="17" y="52" width="20" height="3.5" rx="1.75" fill="#5f6b78" />
  </svg>
}
