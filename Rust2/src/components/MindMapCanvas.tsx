import { useMemo } from "react";

interface MindMapNode {
  label: string;
  children?: MindMapNode[];
}

interface MindMapCanvasProps {
  data: string; // JSON string
  className?: string;
}

interface LayoutNode {
  label: string;
  x: number;
  y: number;
  children: LayoutNode[];
  width: number;
  height: number;
}

const NODE_W = 160;
const NODE_H = 36;
const H_GAP = 40;
const V_GAP = 12;

function layoutTree(node: MindMapNode, x: number, y: number, depth: number): LayoutNode {
  const children: LayoutNode[] = [];
  let totalHeight = 0;

  if (node.children && node.children.length > 0) {
    const childX = x + NODE_W + H_GAP;
    let cy = y;
    for (const child of node.children) {
      const laid = layoutTree(child, childX, cy, depth + 1);
      children.push(laid);
      cy += laid.height + V_GAP;
      totalHeight += laid.height + V_GAP;
    }
    totalHeight -= V_GAP; // remove last gap
  }

  const selfHeight = Math.max(NODE_H, totalHeight);
  return { label: node.label, x, y, children, width: NODE_W, height: selfHeight };
}

function centerY(n: LayoutNode): number {
  return n.y + n.height / 2;
}

function renderNodes(node: LayoutNode, elements: React.JSX.Element[], depth: number) {
  const cy = centerY(node);
  const colors = [
    "fill-brand-600/20 stroke-brand-500",
    "fill-blue-600/15 stroke-blue-400",
    "fill-emerald-600/15 stroke-emerald-400",
    "fill-amber-600/15 stroke-amber-400",
    "fill-purple-600/15 stroke-purple-400",
  ];
  const color = colors[depth % colors.length];
  const textColor = depth === 0 ? "fill-[var(--active-text)]" : "fill-[var(--text-primary)]";

  elements.push(
    <g key={`${node.x}-${cy}-${node.label}`}>
      <rect
        x={node.x}
        y={cy - NODE_H / 2}
        width={NODE_W}
        height={NODE_H}
        rx={8}
        className={color}
        strokeWidth={1.5}
      />
      <text
        x={node.x + NODE_W / 2}
        y={cy + 1}
        textAnchor="middle"
        dominantBaseline="middle"
        className={`text-[11px] font-medium ${textColor}`}
      >
        {node.label.length > 14 ? node.label.slice(0, 14) + "…" : node.label}
      </text>
    </g>
  );

  for (const child of node.children) {
    const childCy = centerY(child);
    const x1 = node.x + NODE_W;
    const x2 = child.x;
    const mx = (x1 + x2) / 2;

    elements.push(
      <path
        key={`line-${x1}-${cy}-${x2}-${childCy}`}
        d={`M${x1},${cy} C${mx},${cy} ${mx},${childCy} ${x2},${childCy}`}
        fill="none"
        className="stroke-[var(--border-subtle)]"
        strokeWidth={1.5}
      />
    );

    renderNodes(child, elements, depth + 1);
  }
}

export function MindMapCanvas({ data, className }: MindMapCanvasProps) {
  const layout = useMemo(() => {
    try {
      const parsed: MindMapNode = JSON.parse(data);
      return layoutTree(parsed, 20, 20, 0);
    } catch {
      return null;
    }
  }, [data]);

  if (!layout) {
    return (
      <div className={`flex items-center justify-center p-8 text-[var(--text-muted)] text-sm ${className || ""}`}>
        无法解析思维导图数据
      </div>
    );
  }

  // Calculate total SVG size
  function getMaxExtent(n: LayoutNode): { maxX: number; maxY: number } {
    let maxX = n.x + NODE_W + 20;
    let maxY = n.y + n.height + 20;
    for (const c of n.children) {
      const ext = getMaxExtent(c);
      maxX = Math.max(maxX, ext.maxX);
      maxY = Math.max(maxY, ext.maxY);
    }
    return { maxX, maxY };
  }
  const extent = getMaxExtent(layout);

  const elements: React.JSX.Element[] = [];
  renderNodes(layout, elements, 0);

  return (
    <div className={`overflow-auto ${className || ""}`}>
      <svg
        width={extent.maxX}
        height={extent.maxY}
        viewBox={`0 0 ${extent.maxX} ${extent.maxY}`}
        className="min-w-full"
      >
        {elements}
      </svg>
    </div>
  );
}
