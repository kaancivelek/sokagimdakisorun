import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Panel yalnızca kendi sunucusundan .NET API'yi çağırır (ADR-0006); tarayıcıya API adresi verilmez.
  poweredByHeader: false,
  reactStrictMode: true,
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "same-origin" },
          { key: "X-Frame-Options", value: "DENY" },
        ],
      },
    ];
  },
};

export default nextConfig;
