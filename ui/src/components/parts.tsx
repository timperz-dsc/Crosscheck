import type { ButtonHTMLAttributes, ReactNode } from 'react';

/** The Crosscheck mark. */
export function Mark({ size = 28 }: { size?: number }) {
  return (
    <svg viewBox="0 0 100 100" width={size} height={size} aria-hidden="true">
      <path d="M20 50 L36 66" stroke="#111315" strokeWidth="13" strokeLinecap="round" fill="none" />
      <path d="M50 65.5 L82 30" stroke="#138A52" strokeWidth="13" strokeLinecap="round" fill="none" />
    </svg>
  );
}

/** The one alert icon (4-desync). */
export function AlertIcon() {
  return (
    <svg viewBox="0 0 24 24" width="28" height="28" aria-hidden="true">
      <circle cx="12" cy="12" r="10" fill="none" stroke="#B42318" strokeWidth="2" />
      <path d="M12 7 L12 13" stroke="#B42318" strokeWidth="2.2" strokeLinecap="round" fill="none" />
      <circle cx="12" cy="16.8" r="1.3" fill="#B42318" />
    </svg>
  );
}

export function Header({ children }: { children?: ReactNode }) {
  return (
    <header className="header">
      <div className="brand">
        <Mark />
        <span className="brand-name">Crosscheck</span>
      </div>
      {children}
    </header>
  );
}

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'secondary'; small?: boolean };

export function Button({ variant = 'primary', small = false, className, type = 'button', ...rest }: ButtonProps) {
  return <button type={type} className={['btn', `btn-${variant}`, small ? 'btn-small' : '', className ?? ''].join(' ').trim()} {...rest} />;
}

/** One status line under the header. */
export function Line({ children, tone }: { children: ReactNode; tone?: 'warning' }) {
  return (
    <div className="line" role="status">
      <span className={tone === 'warning' ? 'warning' : undefined}>{children}</span>
    </div>
  );
}
