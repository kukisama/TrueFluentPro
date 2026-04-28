import { useState, useRef, useCallback, useEffect } from "react";
import { Play, Pause, SkipBack, SkipForward, Volume2, VolumeX } from "lucide-react";
import { Button, Select } from "./ui";

interface AudioPlayerProps {
  src: string;
  title?: string;
  className?: string;
}

export function AudioPlayer({ src, title, className }: AudioPlayerProps) {
  const audioRef = useRef<HTMLAudioElement>(null);
  const [isPlaying, setIsPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [speed, setSpeed] = useState(1.0);
  const [muted, setMuted] = useState(false);

  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;

    const onTime = () => setCurrentTime(audio.currentTime);
    const onMeta = () => setDuration(audio.duration || 0);
    const onEnd = () => setIsPlaying(false);

    audio.addEventListener("timeupdate", onTime);
    audio.addEventListener("loadedmetadata", onMeta);
    audio.addEventListener("ended", onEnd);
    return () => {
      audio.removeEventListener("timeupdate", onTime);
      audio.removeEventListener("loadedmetadata", onMeta);
      audio.removeEventListener("ended", onEnd);
    };
  }, [src]);

  const togglePlay = useCallback(() => {
    const audio = audioRef.current;
    if (!audio) return;
    if (isPlaying) { audio.pause(); } else { audio.play(); }
    setIsPlaying(!isPlaying);
  }, [isPlaying]);

  const seek = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const audio = audioRef.current;
    if (!audio) return;
    audio.currentTime = Number(e.target.value);
  }, []);

  const skip = useCallback((delta: number) => {
    const audio = audioRef.current;
    if (!audio) return;
    audio.currentTime = Math.max(0, Math.min(audio.duration, audio.currentTime + delta));
  }, []);

  const changeSpeed = useCallback((val: string) => {
    const s = Number(val);
    setSpeed(s);
    if (audioRef.current) audioRef.current.playbackRate = s;
  }, []);

  return (
    <div className={`flex items-center gap-3 px-4 py-2 ${className || ""}`}>
      <audio ref={audioRef} src={src} muted={muted} preload="metadata" />

      <Button variant="ghost" size="icon" className="h-8 w-8" onClick={() => skip(-10)}>
        <SkipBack size={14} />
      </Button>
      <Button variant="secondary" size="icon" className="h-9 w-9" onClick={togglePlay}>
        {isPlaying ? <Pause size={16} /> : <Play size={16} />}
      </Button>
      <Button variant="ghost" size="icon" className="h-8 w-8" onClick={() => skip(10)}>
        <SkipForward size={14} />
      </Button>

      <span className="text-xs text-[var(--text-muted)] tabular-nums w-12 text-right">
        {formatTime(currentTime)}
      </span>
      <input
        type="range"
        min={0}
        max={duration || 0}
        step={0.1}
        value={currentTime}
        onChange={seek}
        className="flex-1 h-1.5 accent-brand-500 cursor-pointer"
      />
      <span className="text-xs text-[var(--text-muted)] tabular-nums w-12">
        {formatTime(duration)}
      </span>

      <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => setMuted(!muted)}>
        {muted ? <VolumeX size={14} /> : <Volume2 size={14} />}
      </Button>
      <Select className="w-16 h-7 text-xs" value={speed.toString()} onChange={(e) => changeSpeed(e.target.value)}>
        {[0.5, 0.75, 1.0, 1.25, 1.5, 2.0].map((s) => <option key={s} value={s}>{s}x</option>)}
      </Select>
      {title && <span className="text-xs text-[var(--text-muted)] truncate max-w-[120px]">{title}</span>}
    </div>
  );
}

function formatTime(sec: number): string {
  if (!sec || !isFinite(sec)) return "0:00";
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}
