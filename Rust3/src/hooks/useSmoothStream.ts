import { useState, useRef, useCallback, useEffect } from "react";

interface SmoothStreamReturn {
  displayedText: string;
  appendToken: (token: string) => void;
  endStream: () => void;
  reset: () => void;
  isStreaming: boolean;
}

/**
 * Character-level smooth streaming hook for typewriter effect.
 *
 * Tokens are buffered and flushed at a fixed tick rate to avoid
 * rendering jank from large code blocks.
 */
export function useSmoothStream(
  options?: { tickMs?: number; maxCharsPerTick?: number },
): SmoothStreamReturn {
  const tickMs = options?.tickMs ?? 200;
  const maxCharsPerTick = options?.maxCharsPerTick ?? 500;

  const [displayedText, setDisplayedText] = useState("");
  const [isStreaming, setIsStreaming] = useState(false);

  const pendingRef = useRef("");
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const endedRef = useRef(false);

  const startTicker = useCallback(() => {
    if (intervalRef.current) return;
    setIsStreaming(true);
    endedRef.current = false;

    intervalRef.current = setInterval(() => {
      if (pendingRef.current.length === 0) {
        if (endedRef.current) {
          // Stream ended and buffer drained
          if (intervalRef.current) {
            clearInterval(intervalRef.current);
            intervalRef.current = null;
          }
          setIsStreaming(false);
        }
        return;
      }

      const chars = Math.min(pendingRef.current.length, maxCharsPerTick);
      const chunk = pendingRef.current.slice(0, chars);
      pendingRef.current = pendingRef.current.slice(chars);

      setDisplayedText((prev) => prev + chunk);
    }, tickMs);
  }, [tickMs, maxCharsPerTick]);

  const appendToken = useCallback(
    (token: string) => {
      pendingRef.current += token;
      startTicker();
    },
    [startTicker],
  );

  const endStream = useCallback(() => {
    endedRef.current = true;
    // Flush remaining buffer immediately
    if (pendingRef.current.length > 0) {
      const remaining = pendingRef.current;
      pendingRef.current = "";
      setDisplayedText((prev) => prev + remaining);
    }
    if (intervalRef.current) {
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    }
    setIsStreaming(false);
  }, []);

  const reset = useCallback(() => {
    if (intervalRef.current) {
      clearInterval(intervalRef.current);
      intervalRef.current = null;
    }
    pendingRef.current = "";
    endedRef.current = false;
    setDisplayedText("");
    setIsStreaming(false);
  }, []);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
      }
    };
  }, []);

  return { displayedText, appendToken, endStream, reset, isStreaming };
}
