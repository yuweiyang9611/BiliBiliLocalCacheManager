import type { SVGProps } from 'react';

export type IconName =
  | 'library' | 'storage' | 'trash' | 'settings' | 'diagnostics'
  | 'search' | 'folder' | 'refresh' | 'play' | 'export' | 'delete'
  | 'restore' | 'scan' | 'stop' | 'check' | 'warning' | 'film';

const paths: Record<IconName, React.ReactNode> = {
  library: <><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v16H6.5A2.5 2.5 0 0 0 4 21.5z" /><path d="M4 5.5v16A2.5 2.5 0 0 1 6.5 19H20" /></>,
  storage: <><ellipse cx="12" cy="5" rx="8" ry="3" /><path d="M4 5v7c0 1.7 3.6 3 8 3s8-1.3 8-3V5" /><path d="M4 12v7c0 1.7 3.6 3 8 3s8-1.3 8-3v-7" /></>,
  trash: <><path d="M3 6h18M8 6V3h8v3m3 0-1 15H6L5 6" /><path d="M10 10v7m4-7v7" /></>,
  settings: <><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1a1.7 1.7 0 0 0 1.9.3A1.7 1.7 0 0 0 10 3V2.8h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1z" /></>,
  diagnostics: <><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" /><path d="M14 2v6h6M8 13h2l1.5-3 2.5 7 1.5-4H18" /></>,
  search: <><circle cx="11" cy="11" r="7" /><path d="m20 20-4-4" /></>,
  folder: <path d="M3 5a2 2 0 0 1 2-2h5l2 3h7a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />,
  refresh: <><path d="M20 7v5h-5" /><path d="M19 12a7 7 0 1 0-2 5" /></>,
  play: <path d="m8 5 11 7-11 7z" />,
  export: <><path d="M12 3v12m-5-5 5 5 5-5" /><path d="M5 21h14a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2" /></>,
  delete: <><path d="M3 6h18M8 6V3h8v3m3 0-1 15H6L5 6" /><path d="M10 10v7m4-7v7" /></>,
  restore: <><path d="M3 12a9 9 0 1 0 3-6.7L3 8" /><path d="M3 3v5h5" /></>,
  scan: <><path d="M4 7V4h3m10 0h3v3M4 17v3h3m10 0h3v-3" /><path d="M7 12h10" /></>,
  stop: <rect x="6" y="6" width="12" height="12" rx="2" />,
  check: <path d="m5 12 4 4L19 6" />,
  warning: <><path d="M12 3 2 21h20z" /><path d="M12 9v5m0 3h.01" /></>,
  film: <><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M7 4v16M17 4v16M3 9h4m10 0h4M3 15h4m10 0h4" /></>,
};

export function Icon({ name, ...props }: { name: IconName } & SVGProps<SVGSVGElement>) {
  return <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" {...props}>{paths[name]}</svg>;
}
