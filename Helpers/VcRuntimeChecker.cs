using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrueFluentPro.Helpers
{
    /// <summary>
    /// 检测 Microsoft Visual C++ 2015-2022 Redistributable (x64) 是否已安装。
    /// Azure Speech SDK 的原生 dll（Microsoft.CognitiveServices.Speech.core.dll）依赖该运行库；
    /// 部分用户机器上没装这个运行库时，调用 Speech SDK 会抛 DllNotFoundException。
    /// </summary>
    public static class VcRuntimeChecker
    {
        public const string DownloadUrlX64 = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        // VC++ 2015-2022 运行库的关键 dll，Speech.core.dll 依赖这些
        private static readonly string[] RequiredDlls =
        {
            "vcruntime140.dll",
            "vcruntime140_1.dll",
            "msvcp140.dll"
        };

        /// <summary>
        /// 通过尝试加载 VC++ 运行库的关键 dll 来检测是否已安装。
        /// 仅在 Windows 上有意义；其它平台直接返回 true（不阻塞）。
        /// </summary>
        public static bool IsX64Installed()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return true;
            }

            foreach (var dll in RequiredDlls)
            {
                try
                {
                    var handle = NativeLibrary.Load(dll);
                    NativeLibrary.Free(handle);
                }
                catch (DllNotFoundException)
                {
                    return false;
                }
                catch
                {
                    // 其他异常按已安装处理，避免误报
                }
            }
            return true;
        }

        /// <summary>
        /// 判断异常是否由于缺少 Speech SDK 原生 dll 导致（典型表现为缺少 VC++ 运行库）。
        /// </summary>
        public static bool IsSpeechCoreDllMissing(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException!)
            {
                if (e is DllNotFoundException || e is TypeInitializationException)
                {
                    var msg = e.Message ?? string.Empty;
                    if (msg.IndexOf("Speech.core.dll", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                if (e.InnerException == null) break;
            }
            return false;
        }

        /// <summary>
        /// 给最终用户的中文友好提示文本。
        /// </summary>
        public static string BuildFriendlyMessage()
        {
            return "启动翻译失败：检测到系统缺少 Microsoft Visual C++ 2015-2022 运行库 (x64)。" +
                   "Azure 语音 SDK 依赖该运行库。请安装后重试。" +
                   $"下载地址：{DownloadUrlX64}";
        }

        /// <summary>
        /// 在默认浏览器中打开下载页面。失败时静默忽略。
        /// </summary>
        public static void TryOpenDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = DownloadUrlX64,
                    UseShellExecute = true
                });
            }
            catch
            {
                // 忽略打开失败
            }
        }
    }
}
