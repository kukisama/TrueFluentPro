import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import rehypeHighlight from "rehype-highlight";
import rehypeSanitize from "rehype-sanitize";

interface MarkdownRendererProps {
  content: string;
  className?: string;
}

export function MarkdownRenderer({ content, className }: MarkdownRendererProps) {
  return (
    <div className={className}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeSanitize, rehypeHighlight]}
        components={{
          pre: ({ children }) => (
            <pre className="rounded-lg bg-[var(--surface-1)] p-3 overflow-x-auto text-xs my-2">
              {children}
            </pre>
          ),
          code: ({ children, className: codeClass }) => {
            const isInline = !codeClass;
            return isInline ? (
              <code className="px-1.5 py-0.5 rounded bg-[var(--surface-2)] text-[var(--active-text)] text-xs font-mono">
                {children}
              </code>
            ) : (
              <code className={codeClass}>{children}</code>
            );
          },
          table: ({ children }) => (
            <div className="overflow-x-auto my-2">
              <table className="w-full border-collapse text-xs">{children}</table>
            </div>
          ),
          th: ({ children }) => (
            <th className="border border-[var(--border-subtle)] bg-[var(--surface-1)] px-3 py-1.5 text-left font-medium">
              {children}
            </th>
          ),
          td: ({ children }) => (
            <td className="border border-[var(--border-subtle)] px-3 py-1.5">{children}</td>
          ),
          a: ({ href, children }) => (
            <a href={href} target="_blank" rel="noopener noreferrer"
              className="text-brand-400 hover:text-brand-300 underline underline-offset-2">
              {children}
            </a>
          ),
          blockquote: ({ children }) => (
            <blockquote className="border-l-3 border-brand-500 pl-3 my-2 text-[var(--text-muted)] italic">
              {children}
            </blockquote>
          ),
          ul: ({ children }) => <ul className="list-disc list-inside space-y-0.5 my-1">{children}</ul>,
          ol: ({ children }) => <ol className="list-decimal list-inside space-y-0.5 my-1">{children}</ol>,
          h1: ({ children }) => <h1 className="text-lg font-bold mt-4 mb-2">{children}</h1>,
          h2: ({ children }) => <h2 className="text-base font-semibold mt-3 mb-1.5">{children}</h2>,
          h3: ({ children }) => <h3 className="text-sm font-semibold mt-2 mb-1">{children}</h3>,
          p: ({ children }) => <p className="my-1 leading-relaxed">{children}</p>,
          hr: () => <hr className="my-3 border-[var(--border-subtle)]" />,
        }}
      >
        {content}
      </ReactMarkdown>
    </div>
  );
}
