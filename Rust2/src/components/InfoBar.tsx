import { useEffect } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { X, Info, AlertTriangle, AlertCircle, CheckCircle2 } from "lucide-react";
import { useAppStore } from "../stores/app-store";

const ICONS = {
  info: <Info size={16} className="text-blue-400" />,
  warning: <AlertTriangle size={16} className="text-amber-400" />,
  error: <AlertCircle size={16} className="text-red-400" />,
  success: <CheckCircle2 size={16} className="text-emerald-400" />,
};

const BG = {
  info: "bg-blue-500/10 border-blue-500/20",
  warning: "bg-amber-500/10 border-amber-500/20",
  error: "bg-red-500/10 border-red-500/20",
  success: "bg-emerald-500/10 border-emerald-500/20",
};

export function InfoBar() {
  const { infoBarOpen, infoBarMessage, infoBarSeverity, hideInfoBar } = useAppStore();

  useEffect(() => {
    if (infoBarOpen) {
      const timer = setTimeout(hideInfoBar, 5000);
      return () => clearTimeout(timer);
    }
  }, [infoBarOpen, hideInfoBar]);

  return (
    <AnimatePresence>
      {infoBarOpen && (
        <motion.div
          initial={{ height: 0, opacity: 0 }}
          animate={{ height: "auto", opacity: 1 }}
          exit={{ height: 0, opacity: 0 }}
          transition={{ duration: 0.2 }}
          className={`border-b ${BG[infoBarSeverity]} overflow-hidden`}
        >
          <div className="flex items-center gap-2 px-4 py-2">
            {ICONS[infoBarSeverity]}
            <span className="text-xs text-[var(--text-primary)] flex-1">{infoBarMessage}</span>
            <button onClick={hideInfoBar} className="text-[var(--text-muted)] hover:text-[var(--text-primary)] transition-colors">
              <X size={14} />
            </button>
          </div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
