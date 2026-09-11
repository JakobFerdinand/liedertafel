import type { NextConfig } from "next";
import { PHASE_DEVELOPMENT_SERVER } from "next/constants";

export default function config(phase: string): NextConfig {
  const development = phase === PHASE_DEVELOPMENT_SERVER;
  const api = process.env.ARCHIVE_API_URL;
  if (development && !api) {
    throw new Error(
      "ARCHIVE_API_URL is required. Start the frontend through Aspire AppHost.",
    );
  }
  return {
    output: development ? undefined : "export",
    trailingSlash: true,
    // Separate outputs allow a production build while the dev server is running.
    distDir: development ? ".next-dev" : ".next",
    ...(development
      ? {
          async rewrites() {
            return {
              beforeFiles: [
                { source: "/api/:path*", destination: `${api}/api/:path*` },
              ],
            };
          },
        }
      : {}),
  };
}
