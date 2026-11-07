import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "모아 포켓 · 내 선택을 함께",
  description: "휴대폰에서 만나는 모아. 가진 옷과 남겨둘 돈까지 연결하는 모의 구매 체험.",
  icons: {
    icon: "/favicon.svg",
    shortcut: "/favicon.svg",
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="ko">
      <body className="antialiased">{children}</body>
    </html>
  );
}
