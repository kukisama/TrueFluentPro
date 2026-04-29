import { useState, useEffect, useRef } from "react";

interface FaviconImageProps {
  hostname: string;
  size?: number;
  className?: string;
}

// Module-level cache to avoid duplicate requests
const faviconCache = new Map<string, string | null>();

/** Website favicon with first-letter fallback. */
export function FaviconImage({ hostname, size = 16, className }: FaviconImageProps) {
  const [imgSrc, setImgSrc] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  const firstLetter = (hostname || "?")[0].toUpperCase();

  useEffect(() => {
    if (!hostname) return;

    // Check cache first
    if (faviconCache.has(hostname)) {
      const cached = faviconCache.get(hostname)!;
      if (cached) {
        setImgSrc(cached);
        setLoaded(true);
      }
      return;
    }

    // Abort previous request if hostname changed
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    setLoaded(false);
    setImgSrc(null);

    const url = `https://icon.horse/icon/${hostname}`;

    // Use fetch with timeout for preloading
    const timeout = setTimeout(() => controller.abort(), 5000);

    fetch(url, { signal: controller.signal })
      .then((res) => {
        if (!res.ok) throw new Error("fetch failed");
        return res.blob();
      })
      .then((blob) => {
        const objectUrl = URL.createObjectURL(blob);
        faviconCache.set(hostname, objectUrl);
        if (!controller.signal.aborted) {
          setImgSrc(objectUrl);
          setLoaded(true);
        }
      })
      .catch(() => {
        faviconCache.set(hostname, null);
      })
      .finally(() => clearTimeout(timeout));

    return () => {
      controller.abort();
      clearTimeout(timeout);
    };
  }, [hostname]);

  const radius = Math.max(2, size * 0.25);

  return (
    <div
      className={`inline-flex items-center justify-center shrink-0 ${className || ""}`}
      style={{ width: size, height: size, borderRadius: radius }}
    >
      {loaded && imgSrc ? (
        <img
          src={imgSrc}
          alt={hostname}
          width={size}
          height={size}
          style={{ borderRadius: radius }}
          className="object-cover"
        />
      ) : (
        <div
          className="flex items-center justify-center bg-[var(--surface-2)] text-[var(--text-muted)]"
          style={{
            width: size,
            height: size,
            borderRadius: radius,
            fontSize: Math.max(8, size * 0.6),
            fontWeight: 600,
            lineHeight: 1,
          }}
        >
          {firstLetter}
        </div>
      )}
    </div>
  );
}
