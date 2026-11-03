#include "VoiceBrand.h"
#include <cstdio>
#include <vector>
#include <cstring>

#pragma pack(push, 1)
struct IconHeader { WORD reserved, type, count; };
struct IconEntry { BYTE width, height, colors, reserved; WORD planes, bits; DWORD bytes, offset; };
#pragma pack(pop)

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) { std::puts("usage: BrandIconGenerator <output.ico>"); return 2; }
    ULONG_PTR token{}; Gdiplus::GdiplusStartupInput startup;
    if (Gdiplus::GdiplusStartup(&token, &startup, nullptr) != Gdiplus::Ok) return 3;
    std::vector<IconEntry> entries;
    std::vector<std::vector<BYTE>> images;
    DWORD offset = sizeof(IconHeader) + 7 * sizeof(IconEntry);
    for (int size : {16, 24, 32, 48, 64, 128, 256}) {
        Gdiplus::Bitmap image(size, size, PixelFormat32bppARGB);
        {
            Gdiplus::Graphics g(&image); g.Clear(Gdiplus::Color(0, 0, 0, 0));
            g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
            g.SetTextRenderingHint(Gdiplus::TextRenderingHintAntiAliasGridFit);
            VoiceBrand::Draw(g, 0, 0, static_cast<float>(size));
        }
        const DWORD maskStride = ((size + 31) / 32) * 4;
        std::vector<BYTE> bytes(sizeof(BITMAPINFOHEADER) + size * size * 4 + maskStride * size, 0);
        BITMAPINFOHEADER header{sizeof(header), size, size * 2, 1, 32, BI_RGB};
        std::memcpy(bytes.data(), &header, sizeof(header));
        Gdiplus::Rect rect(0, 0, size, size); Gdiplus::BitmapData data{};
        if (image.LockBits(&rect, Gdiplus::ImageLockModeRead, PixelFormat32bppARGB, &data) != Gdiplus::Ok) {
            Gdiplus::GdiplusShutdown(token); return 4;
        }
        for (int y = 0; y < size; ++y) {
            const BYTE* row = static_cast<BYTE*>(data.Scan0) + y * data.Stride;
            BYTE* target = bytes.data() + sizeof(header) + (size - y - 1) * size * 4;
            std::memcpy(target, row, size * 4);
            BYTE* mask = bytes.data() + sizeof(header) + size * size * 4 + (size - y - 1) * maskStride;
            for (int x = 0; x < size; ++x) if (row[x * 4 + 3] == 0) mask[x / 8] |= static_cast<BYTE>(0x80 >> (x % 8));
        }
        image.UnlockBits(&data);
        entries.push_back({static_cast<BYTE>(size), static_cast<BYTE>(size), 0, 0, 1, 32, static_cast<DWORD>(bytes.size()), offset});
        offset += static_cast<DWORD>(bytes.size()); images.push_back(std::move(bytes));
    }
    FILE* file{}; const errno_t open = _wfopen_s(&file, argv[1], L"wb");
    bool ok = open == 0 && file;
    if (ok) {
        IconHeader header{0, 1, static_cast<WORD>(entries.size())};
        ok = std::fwrite(&header, sizeof(header), 1, file) == 1 &&
            std::fwrite(entries.data(), sizeof(IconEntry), entries.size(), file) == entries.size();
        for (const auto& bytes : images) if (std::fwrite(bytes.data(), 1, bytes.size(), file) != bytes.size()) ok = false;
        if (std::fclose(file) != 0) ok = false;
    }
    Gdiplus::GdiplusShutdown(token);
    std::printf("RESULT brand-icon sizes=16,24,32,48,64,128,256 bytes=%lu success=%d\n", offset, ok);
    return ok ? 0 : 1;
}