#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '../../design/宋人书斋原稿资产_20261104'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../Assets/Appearance/InkStudy'),
    [switch]$Overwrite
)

$ErrorActionPreference = 'Stop'
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath (Join-Path $OutputDirectory 'title-calligraphy.png')) -and -not $Overwrite) {
    throw '新主题资产已存在；重制请显式指定 -Overwrite。'
}
& (Join-Path $PSScriptRoot '../SongStudyAssets/Export-Assets.ps1') -SourceDirectory $SourceDirectory -OutputDirectory $OutputDirectory -Overwrite:$Overwrite

# 资产板中的标题与按钮有浅白衬底，单纯按 alpha 裁切仍会保留整格。
# 用各组件真实墨色/金线/朱砂确定外框；源图不动，旧 SongStudy 资产不动。
$references = @(
    [System.Drawing.Bitmap].Assembly.Location
    [System.Drawing.Color].Assembly.Location
    [Console].Assembly.Location
    'System.Drawing'
    'System.Runtime.InteropServices'
    Get-ChildItem -LiteralPath (Split-Path -Parent ([System.Drawing.Bitmap].Assembly.Location)) -Filter 'System.Private.Windows*.dll' |
        Select-Object -ExpandProperty FullName
) | Select-Object -Unique
if (-not ('TrueFluentPro.InkStudyAssetCrop' -as [type])) {
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TrueFluentPro
{
    public static class InkStudyAssetCrop
    {
        public static void Crop(string sourcePath, string targetPath, bool title, bool redOnly)
        {
            Bitmap result;
            using (var source = new Bitmap(sourcePath))
            using (var image = source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb))
            {
                int left = image.Width, top = image.Height, right = -1, bottom = -1;
                var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[image.Width * 4];
                    for (int y = 0; y < image.Height; y++)
                    {
                        IntPtr pointer = IntPtr.Add(data.Scan0, y * data.Stride);
                        Marshal.Copy(pointer, row, 0, row.Length);
                        for (int x = 0; x < image.Width; x++)
                        {
                            int p = x * 4, b = row[p], g = row[p + 1], r = row[p + 2], a = row[p + 3];
                            bool red = r > g + 35 && r > b + 25;
                            bool ink = Math.Max(r, Math.Max(g, b)) < 145;
                            bool gold = r > 110 && r > g + 10 && g > b + 5;
                            bool core = a > 160 && (title ? ink || red : redOnly ? red : gold || red);
                            if (core)
                            {
                                left = Math.Min(left, x); top = Math.Min(top, y);
                                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                            }
                            // 书法只保留墨迹和印记，不把生成的浅灰题签衬底带入界面。
                            if (title && !red)
                            {
                                int darkness = 255 - Math.Max(r, Math.Max(g, b));
                                row[p + 3] = (byte)(darkness < 110 ? 0 : Math.Min(a, Math.Min(255, (darkness - 90) * 255 / 165)));
                                row[p] = row[p + 1] = row[p + 2] = 28;
                            }
                            if (red && a > 100 && sourcePath.EndsWith("seal-primary.png", StringComparison.OrdinalIgnoreCase))
                            {
                                // 印面应为实色朱砂；去底误留的半透明不能表现为禁用状态。
                                row[p] = 49; row[p + 1] = 63; row[p + 2] = 172; row[p + 3] = 255;
                            }
                        }
                        if (title || sourcePath.EndsWith("seal-primary.png", StringComparison.OrdinalIgnoreCase))
                            Marshal.Copy(row, 0, pointer, row.Length);
                    }
                }
                finally { image.UnlockBits(data); }
                if (right < left || bottom < top) throw new InvalidOperationException("没有识别到组件前景：" + sourcePath);
                left = Math.Max(0, left - 6); top = Math.Max(0, top - 6);
                right = Math.Min(image.Width - 1, right + 6); bottom = Math.Min(image.Height - 1, bottom + 6);
                result = image.Clone(Rectangle.FromLTRB(left, top, right + 1, bottom + 1), PixelFormat.Format32bppArgb);
            }
            using (result)
            {
                result.Save(targetPath, ImageFormat.Png);
                Console.WriteLine("COMPONENT " + System.IO.Path.GetFileName(targetPath) + " " + result.Width + "x" + result.Height);
            }
        }
    }
}
'@
}
foreach ($name in 'nav-idle', 'nav-selected', 'panel-frame', 'seal-primary', 'tassel') {
    $path = Join-Path $OutputDirectory "$name.png"
    [TrueFluentPro.InkStudyAssetCrop]::Crop($path, $path, $false, ($name -in @('seal-primary', 'tassel')))
}
$titlePath = Join-Path $OutputDirectory 'title-calligraphy.png'
[TrueFluentPro.InkStudyAssetCrop]::Crop((Join-Path $OutputDirectory 'bamboo-right.png'), $titlePath, $true, $false)
# 只有本次制造的临时格被移除，源图与其他主题均保留。
Remove-Item -LiteralPath (Join-Path $OutputDirectory 'bamboo-right.png')