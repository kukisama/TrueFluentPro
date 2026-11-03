#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <objidl.h>
#include <gdiplus.h>

// Original artwork shared by the small Win32 toolbar and the embedded ICO maker.
namespace VoiceBrand {
inline void RoundRect(Gdiplus::GraphicsPath& path, float x, float y, float w, float h, float r) {
    const float d = r * 2;
    path.AddArc(x, y, d, d, 180, 90); path.AddArc(x + w - d, y, d, d, 270, 90);
    path.AddArc(x + w - d, y + h - d, d, d, 0, 90); path.AddArc(x, y + h - d, d, d, 90, 90);
    path.CloseFigure();
}
inline void Draw(Gdiplus::Graphics& g, float x, float y, float size) {
    using namespace Gdiplus;
    GraphicsPath tile; RoundRect(tile, x, y, size, size, size * .23f);
    LinearGradientBrush blue(PointF(x, y), PointF(x, y + size), Color(255, 45, 135, 255), Color(255, 28, 107, 250));
    g.FillPath(&blue, &tile);
    GraphicsPath bubble(FillModeWinding); RoundRect(bubble, x + size * .18f, y + size * .19f, size * .64f, size * .57f, size * .10f);
    PointF tail[] = {{x + size * .30f, y + size * .67f}, {x + size * .30f, y + size * .87f}, {x + size * .50f, y + size * .71f}};
    bubble.AddPolygon(tail, 3);
    SolidBrush white(Color(255, 244, 250, 255)); g.FillPath(&white, &bubble);
    Font font(L"Microsoft YaHei UI", size * .45f, FontStyleBold, UnitPixel);
    StringFormat format; format.SetAlignment(StringAlignmentCenter); format.SetLineAlignment(StringAlignmentCenter);
    SolidBrush ink(Color(255, 33, 120, 255));
    g.DrawString(L"译", 1, &font, RectF(x + size * .18f, y + size * .18f, size * .64f, size * .57f), &format, &ink);
}
}