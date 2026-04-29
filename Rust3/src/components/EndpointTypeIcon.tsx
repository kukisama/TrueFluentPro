interface EndpointTypeIconProps {
  endpointType: string;
  size?: number;
  className?: string;
}

// Azure star icon — simplified from Microsoft brand assets
const AZURE_STAR_PATH =
  "M12 2L14.5 8.5L21 9.5L16.5 14L17.5 21L12 17.5L6.5 21L7.5 14L3 9.5L9.5 8.5L12 2Z";

// Gateway arrow — simplified network gateway icon
const GATEWAY_PATH =
  "M4 12h6m4 0h6M14 12l-3-3m3 3l-3 3M7 4v4m0 8v4M17 4v4m0 8v4M7 8a1 1 0 100-2 1 1 0 000 2zm0 12a1 1 0 100-2 1 1 0 000 2zm10-12a1 1 0 100-2 1 1 0 000 2zm0 12a1 1 0 100-2 1 1 0 000 2z";

// OpenAI-style swirl — simplified helix shape
const OPENAI_SWIRL_PATH =
  "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 3c1.66 0 3.14.69 4.22 1.78L12 11V5zm-5.22 3.22L11 12H5c0-1.66.69-3.14 1.78-4.22zM5 13h6l-4.22 4.22A6.978 6.978 0 015 13zm7 6c-1.66 0-3.14-.69-4.22-1.78L12 13v6zm5.22-3.22L13 12h6c0 1.66-.69 3.14-1.78 4.22zM13 5v6l4.22-4.22A6.978 6.978 0 0113 5z";

const TYPE_CONFIG: Record<string, { bg: string; path: string }> = {
  azure_open_ai: { bg: "#0078D4", path: AZURE_STAR_PATH },
  azure_openai: { bg: "#0078D4", path: AZURE_STAR_PATH },
  api_management_gateway: { bg: "#6D28D9", path: GATEWAY_PATH },
  apim_gateway: { bg: "#6D28D9", path: GATEWAY_PATH },
};

const DEFAULT_CONFIG = { bg: "#10A37F", path: OPENAI_SWIRL_PATH };

/** Endpoint type icon with color-coded background and SVG glyph. */
export function EndpointTypeIcon({
  endpointType,
  size = 32,
  className,
}: EndpointTypeIconProps) {
  const config = TYPE_CONFIG[endpointType] || DEFAULT_CONFIG;
  const radius = Math.max(8, size * 0.28);
  const iconPad = size * 0.18;

  return (
    <div
      className={`inline-flex items-center justify-center shrink-0 ${className || ""}`}
      style={{
        width: size,
        height: size,
        borderRadius: radius,
        backgroundColor: config.bg,
      }}
    >
      <svg
        viewBox="0 0 24 24"
        fill="none"
        style={{ width: size - iconPad * 2, height: size - iconPad * 2 }}
      >
        {endpointType === "api_management_gateway" ||
        endpointType === "apim_gateway" ? (
          <path
            d={config.path}
            stroke="white"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        ) : (
          <path d={config.path} fill="white" />
        )}
      </svg>
    </div>
  );
}
