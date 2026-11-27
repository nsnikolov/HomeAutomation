import { useSvgId } from './svgId'

/** A battery whose fill shows the state of charge, with a lightning bolt while charging. An unknown level leaves it empty. */
export function Battery({ level, charging }: { level: number | null; charging: boolean }) {
  const id = useSvgId('battery')
  const clamped = level === null || !Number.isFinite(level) ? 0 : Math.min(100, Math.max(0, level))
  const inner = { x: 21, y: 14, width: 22, height: 40 }
  const height = inner.height * clamped / 100
  const tone = clamped <= 20 ? ['#f08a6b', '#c2412b'] : clamped <= 50 ? ['#f7cf5a', '#d39a12'] : ['#5fd39a', '#16955f']
  return <svg viewBox="0 0 64 64" width="100%" height="100%" aria-hidden="true" focusable="false" className="flow-illustration">
    <defs>
      <linearGradient id={`${id}-fill`} x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor={tone[0]} />
        <stop offset="1" stopColor={tone[1]} />
      </linearGradient>
      <linearGradient id={`${id}-case`} x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor="#eef2f5" />
        <stop offset="1" stopColor="#c6d0d9" />
      </linearGradient>
    </defs>
    <rect x="26" y="5" width="12" height="5" rx="1.5" fill="#7d8a97" />
    <rect x="17" y="9" width="30" height="50" rx="5" fill={`url(#${id}-case)`} stroke="#6f7c89" strokeWidth="1.4" />
    <rect className="flow-battery-fill" x={inner.x} y={inner.y + inner.height - height} width={inner.width} height={height} rx="2.5"
      fill={`url(#${id}-fill)`} />
    <rect x={inner.x} y={inner.y} width="5" height={inner.height} rx="2.5" fill="#fff" opacity="0.25" />
    {charging && <path d="M34 19 L25.5 35 H31.5 L28.5 49 L38.5 31 H32.5 Z" fill="#fff8d6" stroke="#8a5a00" strokeWidth="1.2"
      strokeLinejoin="round" />}
  </svg>
}
