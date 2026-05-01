"use client";

import type { CanvasDocument, Shape } from "@acme/shared-types";

interface CanvasPreviewProps {
  doc: CanvasDocument;
  className?: string;
  background?: string;
}

export function CanvasPreview({ doc, className, background }: CanvasPreviewProps) {
  const sorted = [...doc.shapes].sort((a, b) => a.zIndex - b.zIndex);

  return (
    <svg
      viewBox={`0 0 ${doc.width} ${doc.height}`}
      preserveAspectRatio="xMidYMid meet"
      className={className}
      style={{ display: "block", background: background ?? doc.background }}
      role="img"
      aria-label={`Canvas preview: ${doc.name}`}
    >
      {sorted.map((shape) => (
        <ShapeSvg key={shape.id} shape={shape} />
      ))}
    </svg>
  );
}

function ShapeSvg({ shape }: { shape: Shape }) {
  const transform = `translate(${shape.position.x} ${shape.position.y}) rotate(${shape.rotation}) scale(${shape.scale.x} ${shape.scale.y})`;

  switch (shape.kind) {
    case "rect":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <rect
            width={shape.width}
            height={shape.height}
            fill={shape.fill}
            stroke={shape.stroke}
            strokeWidth={shape.strokeWidth ?? 0}
            rx={shape.cornerRadius ?? 0}
            ry={shape.cornerRadius ?? 0}
          />
        </g>
      );

    case "circle":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <circle
            r={shape.radius}
            fill={shape.fill}
            stroke={shape.stroke}
            strokeWidth={shape.strokeWidth ?? 0}
          />
        </g>
      );

    case "line": {
      const pts: string[] = [];
      for (let i = 0; i + 1 < shape.points.length; i += 2) {
        pts.push(`${shape.points[i]},${shape.points[i + 1]}`);
      }
      const points = pts.join(" ");
      return (
        <g transform={transform} opacity={shape.opacity}>
          {shape.closed ? (
            <polygon
              points={points}
              fill={shape.stroke}
              stroke={shape.stroke}
              strokeWidth={shape.strokeWidth}
            />
          ) : (
            <polyline
              points={points}
              fill="none"
              stroke={shape.stroke}
              strokeWidth={shape.strokeWidth}
              strokeLinecap="round"
            />
          )}
        </g>
      );
    }

    case "text":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <foreignObject
            x={0}
            y={0}
            width={shape.width ?? 99999}
            height={99999}
          >
            <div
              style={{
                fontSize: `${shape.fontSize}px`,
                fontFamily: shape.fontFamily,
                color: shape.fill,
                textAlign: shape.align ?? "left",
                lineHeight: 1.15,
                whiteSpace: shape.width ? "normal" : "pre",
                wordBreak: "break-word",
                margin: 0,
              }}
            >
              {shape.text}
            </div>
          </foreignObject>
        </g>
      );

    case "image":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <image
            href={shape.blobKey}
            width={shape.width}
            height={shape.height}
            preserveAspectRatio="xMidYMid meet"
          />
        </g>
      );

    case "group":
      return null;
  }
}
