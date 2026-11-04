#Requires -Version 7.0
<#
.SYNOPSIS
将三张书斋源图制造为运行时 PNG；不调用云端，不清理任何目录。
.DESCRIPTION
输入必须均为 2560x1440。书册固定切为 5x3；两张 2x2 资产板去品红底并修整透明边界。
默认拒绝覆盖任何已有目标文件；只有显式 -Overwrite 才允许重制。
#>
[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '../../design/宋人书斋资产源图_20261104'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../Assets/Appearance/SongStudy'),
    [switch]$Overwrite
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw '此制造脚本使用 System.Drawing，请在 Windows 下运行。'
}

$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Add-Type -AssemblyName System.Drawing.Common

if (-not ('TrueFluentPro.SongStudyAssets.AssetExporter' -as [type])) {
    # Marshal.Copy 按行传输，像素计算全部在托管 C# 内；不需要 unsafe 编译参数。
    $references = @(
        [System.Drawing.Bitmap].Assembly.Location
        [System.Drawing.Color].Assembly.Location
        'System.Drawing'
        'System.Collections'
        'System.Runtime.InteropServices'
        # .NET 10 把 GDI+ 接口分到这些实现程序集；旧宿主没有时无需添加。
        Get-ChildItem -LiteralPath (Split-Path -Parent ([System.Drawing.Bitmap].Assembly.Location)) -Filter 'System.Private.Windows*.dll' |
            Select-Object -ExpandProperty FullName
    ) | Select-Object -Unique

    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace TrueFluentPro.SongStudyAssets
{
    public static class AssetExporter
    {
        private sealed class OutputImage : IDisposable
        {
            public readonly string Name;
            public readonly Bitmap Image;

            public OutputImage(string name, Bitmap image)
            {
                Name = name;
                Image = image;
            }

            public void Dispose() { Image.Dispose(); }
        }

        public static int Export(string sourceDirectory, string outputDirectory, bool overwrite)
        {
            string[] controls = { "nav-idle.png", "nav-selected.png", "seal-primary.png", "panel-frame.png" };
            string[] ornaments = { "bamboo-left.png", "landscape.png", "bamboo-right.png", "tassel.png" };
            int[] xs = { 0, 48, 1248, 1312, 2512, 2560 };
            int[] ys = { 0, 64, 1376, 1440 };
            var names = new List<string>();
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 5; col++)
                    names.Add(string.Format("book-r{0}-c{1}.png", row, col));
            names.AddRange(controls);
            names.AddRange(ornaments);

            // 先检查所有目标；无 Overwrite 时不会因后面的冲突而先写一部分图片。
            foreach (string name in names)
            {
                string path = Path.Combine(outputDirectory, name);
                if (Directory.Exists(path))
                    throw new IOException("目标图片路径被目录占用：" + path);
                if (!overwrite && File.Exists(path))
                    throw new IOException("目标图片已存在；如需重制，请显式指定 -Overwrite：" + path);
            }

            var outputs = new List<OutputImage>();
            try
            {
                using (Bitmap book = LoadSource(Path.Combine(sourceDirectory, "book-spread.png")))
                using (Bitmap controlAtlas = LoadSource(Path.Combine(sourceDirectory, "controls-atlas.png")))
                using (Bitmap ornamentAtlas = LoadSource(Path.Combine(sourceDirectory, "ornaments-atlas.png")))
                {
                    for (int row = 0; row < 3; row++)
                    {
                        for (int col = 0; col < 5; col++)
                        {
                            string name = string.Format("book-r{0}-c{1}.png", row, col);
                            Bitmap slice = Crop(book, new Rectangle(xs[col], ys[row],
                                xs[col + 1] - xs[col], ys[row + 1] - ys[row]));
                            try
                            {
                                VisibleBounds(slice, false, name);
                                outputs.Add(new OutputImage(name, slice));
                            }
                            catch
                            {
                                slice.Dispose();
                                throw;
                            }
                        }
                    }

                    StageAtlas(controlAtlas, controls, outputs);
                    StageAtlas(ornamentAtlas, ornaments, outputs);
                }

                // 所有源图尺寸、非空与去底制造均已成功才开始写目标 PNG。
                Directory.CreateDirectory(outputDirectory);
                foreach (OutputImage output in outputs)
                {
                    string path = Path.Combine(outputDirectory, output.Name);
                    // CreateNew 同时拒绝预检之后出现的文件；绝不删除旧图或无关文件。
                    using (var stream = new FileStream(path,
                        overwrite ? FileMode.Create : FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        output.Image.Save(stream, ImageFormat.Png);
                    }
                }
                return outputs.Count;
            }
            finally
            {
                foreach (OutputImage output in outputs)
                    output.Dispose();
            }
        }

        private static Bitmap LoadSource(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到书斋源图：" + path, path);

            var image = new Bitmap(path);
            if (image.Width != 2560 || image.Height != 1440)
            {
                string size = image.Width + "x" + image.Height;
                image.Dispose();
                throw new InvalidDataException("源图必须为 2560x1440，实际为 " + size + "：" + path);
            }
            return image;
        }

        private static Bitmap Crop(Bitmap source, Rectangle area)
        {
            var result = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.DrawImage(source, new Rectangle(0, 0, area.Width, area.Height),
                        area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static void StageAtlas(Bitmap atlas, string[] names, List<OutputImage> outputs)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var cell = new Rectangle((i % 2) * 1280, (i / 2) * 720, 1280, 720);
                using (Bitmap cutout = Crop(atlas, cell))
                {
                    Rectangle bounds = VisibleBounds(cutout, true, names[i]);
                    outputs.Add(new OutputImage(names[i], Crop(cutout, bounds)));
                }
            }
        }

        private static Rectangle VisibleBounds(Bitmap bitmap, bool removeMagenta, string name)
        {
            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[bitmap.Width * 4];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    IntPtr address = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(address, row, 0, row.Length);
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        int p = x * 4;
                        int b = row[p], g = row[p + 1], r = row[p + 2], alpha = row[p + 3];
                        if (removeMagenta)
                        {
                            // 只以红、蓝同时显著高于绿色作为品红底证据。
                            // 纯白的三通道相同，朱砂红的蓝通道不占优，均不会被当底色。
                            int key = Math.Min(r, b) - g;
                            if (key > 24)
                            {
                                double coverage = 1.0 - key / 255.0;
                                alpha = (int)Math.Round(alpha * coverage);
                                if (alpha > 8)
                                {
                                    // 对抗锯齿边缘反解品红混色，保留前景的浅白和半透明细线。
                                    row[p] = Clamp((b - key) / coverage);
                                    row[p + 1] = Clamp(g / coverage);
                                    row[p + 2] = Clamp((r - key) / coverage);
                                }
                            }
                            if (alpha <= 8)
                            {
                                alpha = 0;
                                row[p] = row[p + 1] = row[p + 2] = 0;
                            }
                            row[p + 3] = (byte)alpha;
                        }

                        // 生成底色有细微色差，去底后低 alpha 残留不能撑满整格。
                        // 用可靠前景确定裁切框，最后加安全边保留抗锯齿与细丝。
                        if (alpha > (removeMagenta ? 128 : 8))
                        {
                            left = Math.Min(left, x);
                            top = Math.Min(top, y);
                            right = Math.Max(right, x);
                            bottom = Math.Max(bottom, y);
                        }
                    }
                    if (removeMagenta)
                        Marshal.Copy(row, 0, address, row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            if (right < left || bottom < top)
                throw new InvalidDataException("切片全空或去底后没有可用内容：" + name);
            if (removeMagenta)
            {
                left = Math.Max(0, left - 6);
                top = Math.Max(0, top - 6);
                right = Math.Min(bitmap.Width - 1, right + 6);
                bottom = Math.Min(bitmap.Height - 1, bottom + 6);
            }
            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }

        private static byte Clamp(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value)));
        }
    }
}
'@
}

$count = [TrueFluentPro.SongStudyAssets.AssetExporter]::Export($SourceDirectory, $OutputDirectory, $Overwrite.IsPresent)
Write-Host "已制造 $count 张书斋 PNG：$OutputDirectory"