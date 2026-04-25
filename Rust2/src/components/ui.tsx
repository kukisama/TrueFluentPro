/*  ────────────────────────────────────────────
    译见 Pro — 现代 UI 组件集
    基于 Radix UI + Tailwind + Framer Motion
    玻璃拟态 + 微动效 + 科技美学
    ──────────────────────────────────────────── */

import React, { forwardRef } from "react";
import * as TabsPrimitive from "@radix-ui/react-tabs";
import * as SwitchPrimitive from "@radix-ui/react-switch";
import * as ProgressPrimitive from "@radix-ui/react-progress";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import * as SeparatorPrimitive from "@radix-ui/react-separator";
import * as ScrollAreaPrimitive from "@radix-ui/react-scroll-area";
import { motion, AnimatePresence } from "framer-motion";
import { cn } from "../lib/utils";

// ─── Button ───

interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "primary" | "secondary" | "ghost" | "danger";
  size?: "sm" | "md" | "lg" | "icon";
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant = "primary", size = "md", children, ...props }, ref) => (
    <button
      ref={ref}
      className={cn(
        "inline-flex items-center justify-center gap-2 rounded-xl font-medium transition-all duration-200 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand-500/50 disabled:opacity-40 disabled:pointer-events-none active:scale-[0.97]",
        {
          "bg-brand-600 text-white hover:bg-brand-500 shadow-lg shadow-brand-600/20": variant === "primary",
          "bg-white/5 text-slate-200 hover:bg-white/10 border border-white/10": variant === "secondary",
          "text-slate-400 hover:text-slate-200 hover:bg-white/5": variant === "ghost",
          "bg-red-600/80 text-white hover:bg-red-500 shadow-lg shadow-red-600/20": variant === "danger",
        },
        {
          "h-7 px-2.5 text-xs rounded-lg": size === "sm",
          "h-9 px-4 text-sm": size === "md",
          "h-11 px-6 text-sm": size === "lg",
          "h-9 w-9 p-0": size === "icon",
        },
        className,
      )}
      {...props}
    >
      {children}
    </button>
  ),
);
Button.displayName = "Button";

// ─── GlassCard ───

interface GlassCardProps extends React.HTMLAttributes<HTMLDivElement> {
  glow?: boolean;
}

export const GlassCard = forwardRef<HTMLDivElement, GlassCardProps>(
  ({ className, glow, children, ...props }, ref) => (
    <div
      ref={ref}
      className={cn(
        "rounded-2xl border border-white/[0.06] bg-white/[0.03] backdrop-blur-xl p-5",
        glow && "shadow-[0_0_40px_-12px] shadow-brand-500/10",
        className,
      )}
      {...props}
    >
      {children}
    </div>
  ),
);
GlassCard.displayName = "GlassCard";

// ─── Input ───

export const Input = forwardRef<
  HTMLInputElement,
  React.InputHTMLAttributes<HTMLInputElement>
>(({ className, ...props }, ref) => (
  <input
    ref={ref}
    className={cn(
      "h-9 w-full rounded-xl border border-white/10 bg-white/[0.04] px-3 text-sm text-slate-200 placeholder:text-slate-500 transition-colors focus:outline-none focus:border-brand-500/50 focus:ring-1 focus:ring-brand-500/30",
      className,
    )}
    {...props}
  />
));
Input.displayName = "Input";

// ─── Textarea ───

export const Textarea = forwardRef<
  HTMLTextAreaElement,
  React.TextareaHTMLAttributes<HTMLTextAreaElement>
>(({ className, ...props }, ref) => (
  <textarea
    ref={ref}
    className={cn(
      "w-full rounded-xl border border-white/10 bg-white/[0.04] px-3 py-2 text-sm text-slate-200 placeholder:text-slate-500 transition-colors focus:outline-none focus:border-brand-500/50 focus:ring-1 focus:ring-brand-500/30 resize-none",
      className,
    )}
    {...props}
  />
));
Textarea.displayName = "Textarea";

// ─── Select ───

export const Select = forwardRef<
  HTMLSelectElement,
  React.SelectHTMLAttributes<HTMLSelectElement>
>(({ className, children, ...props }, ref) => (
  <select
    ref={ref}
    className={cn(
      "h-9 rounded-xl border border-white/10 bg-white/[0.04] px-3 text-sm text-slate-200 transition-colors focus:outline-none focus:border-brand-500/50 appearance-none cursor-pointer",
      className,
    )}
    {...props}
  >
    {children}
  </select>
));
Select.displayName = "Select";

// ─── Label ───

export function Label({ className, children, ...props }: React.LabelHTMLAttributes<HTMLLabelElement>) {
  return (
    <label className={cn("block text-xs font-medium text-slate-400 mb-1.5 uppercase tracking-wider", className)} {...props}>
      {children}
    </label>
  );
}

// ─── Badge ───

interface BadgeProps extends React.HTMLAttributes<HTMLSpanElement> {
  variant?: "default" | "blue" | "green" | "red" | "amber" | "gray";
}

export function Badge({ className, variant = "default", children, ...props }: BadgeProps) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium",
        {
          "bg-white/10 text-slate-300": variant === "default",
          "bg-blue-500/15 text-blue-400 ring-1 ring-blue-500/20": variant === "blue",
          "bg-emerald-500/15 text-emerald-400 ring-1 ring-emerald-500/20": variant === "green",
          "bg-red-500/15 text-red-400 ring-1 ring-red-500/20": variant === "red",
          "bg-amber-500/15 text-amber-400 ring-1 ring-amber-500/20": variant === "amber",
          "bg-slate-500/15 text-slate-400 ring-1 ring-slate-500/20": variant === "gray",
        },
        className,
      )}
      {...props}
    >
      {children}
    </span>
  );
}

// ─── Tabs (Radix) ───

export const Tabs = TabsPrimitive.Root;

export const TabsList = forwardRef<
  React.ComponentRef<typeof TabsPrimitive.List>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.List>
>(({ className, ...props }, ref) => (
  <TabsPrimitive.List
    ref={ref}
    className={cn(
      "inline-flex items-center gap-1 rounded-xl bg-white/[0.04] p-1",
      className,
    )}
    {...props}
  />
));
TabsList.displayName = "TabsList";

export const TabsTrigger = forwardRef<
  React.ComponentRef<typeof TabsPrimitive.Trigger>,
  React.ComponentPropsWithoutRef<typeof TabsPrimitive.Trigger>
>(({ className, ...props }, ref) => (
  <TabsPrimitive.Trigger
    ref={ref}
    className={cn(
      "inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium text-slate-400 transition-all duration-200 hover:text-slate-200 data-[state=active]:bg-brand-600/20 data-[state=active]:text-brand-300 data-[state=active]:shadow-sm",
      className,
    )}
    {...props}
  />
));
TabsTrigger.displayName = "TabsTrigger";

export const TabsContent = TabsPrimitive.Content;

// ─── Switch (Radix) ───

export const Switch = forwardRef<
  React.ComponentRef<typeof SwitchPrimitive.Root>,
  React.ComponentPropsWithoutRef<typeof SwitchPrimitive.Root>
>(({ className, ...props }, ref) => (
  <SwitchPrimitive.Root
    ref={ref}
    className={cn(
      "peer inline-flex h-5 w-10 shrink-0 cursor-pointer items-center rounded-full border border-white/10 transition-colors duration-200 focus-visible:outline-none data-[state=checked]:bg-brand-600 data-[state=unchecked]:bg-white/10",
      className,
    )}
    {...props}
  >
    <SwitchPrimitive.Thumb
      className="pointer-events-none block h-4 w-4 rounded-full bg-white shadow-lg transition-transform duration-200 data-[state=checked]:translate-x-5 data-[state=unchecked]:translate-x-0.5"
    />
  </SwitchPrimitive.Root>
));
Switch.displayName = "Switch";

// ─── Progress (Radix) ───

export const Progress = forwardRef<
  React.ComponentRef<typeof ProgressPrimitive.Root>,
  React.ComponentPropsWithoutRef<typeof ProgressPrimitive.Root> & { indicatorClassName?: string }
>(({ className, value, indicatorClassName, ...props }, ref) => (
  <ProgressPrimitive.Root
    ref={ref}
    className={cn("relative h-1.5 w-full overflow-hidden rounded-full bg-white/10", className)}
    {...props}
  >
    <ProgressPrimitive.Indicator
      className={cn("h-full rounded-full transition-all duration-500 ease-out bg-brand-500", indicatorClassName)}
      style={{ width: `${value ?? 0}%` }}
    />
  </ProgressPrimitive.Root>
));
Progress.displayName = "Progress";

// ─── Tooltip (Radix) ───

export const TooltipProvider = TooltipPrimitive.Provider;
export const Tooltip = TooltipPrimitive.Root;
export const TooltipTrigger = TooltipPrimitive.Trigger;

export const TooltipContent = forwardRef<
  React.ComponentRef<typeof TooltipPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof TooltipPrimitive.Content>
>(({ className, sideOffset = 6, ...props }, ref) => (
  <TooltipPrimitive.Content
    ref={ref}
    sideOffset={sideOffset}
    className={cn(
      "z-50 overflow-hidden rounded-lg bg-slate-800 px-3 py-1.5 text-xs text-slate-200 shadow-xl border border-white/10 animate-in fade-in-0 zoom-in-95",
      className,
    )}
    {...props}
  />
));
TooltipContent.displayName = "TooltipContent";

// ─── Separator ───

export const Separator = forwardRef<
  React.ComponentRef<typeof SeparatorPrimitive.Root>,
  React.ComponentPropsWithoutRef<typeof SeparatorPrimitive.Root>
>(({ className, orientation = "horizontal", ...props }, ref) => (
  <SeparatorPrimitive.Root
    ref={ref}
    decorative
    orientation={orientation}
    className={cn(
      "shrink-0 bg-white/[0.06]",
      orientation === "horizontal" ? "h-px w-full my-4" : "h-full w-px mx-4",
      className,
    )}
    {...props}
  />
));
Separator.displayName = "Separator";

// ─── ScrollArea (Radix) ───

export const ScrollArea = forwardRef<
  React.ComponentRef<typeof ScrollAreaPrimitive.Root>,
  React.ComponentPropsWithoutRef<typeof ScrollAreaPrimitive.Root>
>(({ className, children, ...props }, ref) => (
  <ScrollAreaPrimitive.Root
    ref={ref}
    className={cn("relative overflow-hidden", className)}
    {...props}
  >
    <ScrollAreaPrimitive.Viewport className="h-full w-full rounded-[inherit]">
      {children}
    </ScrollAreaPrimitive.Viewport>
    <ScrollAreaPrimitive.Scrollbar
      orientation="vertical"
      className="flex touch-none select-none p-0.5 transition-opacity duration-150 data-[state=visible]:opacity-100 data-[state=hidden]:opacity-0 w-2"
    >
      <ScrollAreaPrimitive.Thumb className="relative flex-1 rounded-full bg-white/20 hover:bg-white/30" />
    </ScrollAreaPrimitive.Scrollbar>
  </ScrollAreaPrimitive.Root>
));
ScrollArea.displayName = "ScrollArea";

// ─── MotionDiv — 带进场动画的容器 ───

export function FadeIn({
  children,
  className,
  delay = 0,
}: {
  children: React.ReactNode;
  className?: string;
  delay?: number;
}) {
  return (
    <motion.div
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.3, delay, ease: [0.25, 0.46, 0.45, 0.94] }}
      className={className}
    >
      {children}
    </motion.div>
  );
}

// ─── Empty State ───

export function EmptyState({
  icon,
  title,
  description,
  action,
}: {
  icon: React.ReactNode;
  title: string;
  description?: string;
  action?: React.ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center h-full text-center py-16">
      <div className="text-slate-600 mb-5">{icon}</div>
      <p className="text-lg font-medium text-slate-400">{title}</p>
      {description && <p className="text-sm text-slate-500 mt-1.5 max-w-sm">{description}</p>}
      {action && <div className="mt-5">{action}</div>}
    </div>
  );
}

// ─── Section Header ───

export function SectionHeader({
  title,
  description,
  action,
}: {
  title: string;
  description?: string;
  action?: React.ReactNode;
}) {
  return (
    <div className="flex items-center justify-between mb-5">
      <div>
        <h2 className="text-sm font-semibold text-slate-200 uppercase tracking-wider">{title}</h2>
        {description && <p className="text-xs text-slate-500 mt-0.5">{description}</p>}
      </div>
      {action}
    </div>
  );
}

// ─── Animated presence wrapper ───

export { AnimatePresence, motion };
