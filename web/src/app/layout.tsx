import type { Metadata } from "next";

import "./globals.css";

export const metadata: Metadata = {
  title: { default: "Sokağımdaki Sorun — Yönetim Paneli", template: "%s · Sokağımdaki Sorun" },
  robots: { index: false, follow: false },
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="tr">
      <body>{children}</body>
    </html>
  );
}
