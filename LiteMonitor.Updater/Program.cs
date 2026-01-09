using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text; // ★ 必须添加引用
using System.Threading;

namespace LiteMonitor.Updater
{
    internal class Program
    {
        private const string TaskName = "LiteMonitor_AutoStart";
        private const string ExeName = "LiteMonitor.exe";

        static void Main(string[] args)
        {
            // ★★★ [修复乱码 Step 0] 注册编码提供程序以支持 GBK ★★★
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (args.Length == 0) return;

            string zipFile = args[0];
            string resourcesDir = AppContext.BaseDirectory;

            // ===========================================================
            // 1. 智能定位主程序目录（核心）
            // ===========================================================
            string? baseDir = GetMainProgramDirectory(resourcesDir);

            if (baseDir == null)
            {
                LogError(resourcesDir, "[Fatal] 找不到 LiteMonitor.exe，更新终止！");
                return;
            }

            // ===========================================================
            // 2. 等待主程序退出
            // ===========================================================
            WaitExit("LiteMonitor");

            // ===========================================================
            // 3. 解压到 LiteMonitor/_update_tmp
            // ===========================================================
            string tempDir = Path.Combine(baseDir, "_update_tmp");

            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);

                Directory.CreateDirectory(tempDir);
            }
            catch (Exception ex)
            {
                LogError(baseDir, "无法创建临时目录： " + ex.Message);
                return;
            }

            try
            {
                // ★★★ [修复乱码 Step 1] 显式指定使用 GBK 编码解压 ★★★
                // 逻辑：如果 ZIP 内部标记了 UTF-8，.NET 会优先用 UTF-8；
                // 如果没标记（旧版压缩工具），则使用我们传入的 GBK 解析中文。
                var gbk = Encoding.GetEncoding("GBK");
                ZipFile.ExtractToDirectory(zipFile, tempDir, gbk, true);
            }
            catch (Exception ex)
            {
                LogError(baseDir, "解压失败： " + ex.Message);
                return;
            }

            // ===========================================================
            // 4. 处理 ZIP 的最外层目录 (忽略 LiteMonitor_v1.xx/)
            // ===========================================================
            string realFolder = ResolveZipRoot(tempDir);

            // ===========================================================
            // 5. 覆盖更新文件（保留目录结构）
            // ===========================================================
            try
            {
                foreach (string srcPath in Directory.GetFiles(realFolder, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(realFolder, srcPath);
                    string destPath = Path.Combine(baseDir, rel);

                    // ✔ 无论 ZIP 内 Updater 在根目录或子目录，都不覆盖自己
                    if (rel.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(srcPath, destPath, true);
                }
            }
            catch (Exception ex)
            {
                LogError(baseDir, "复制更新文件失败：" + ex.Message);
            }

            // ===========================================================
            // 6. 清理临时目录 & zip
            // ===========================================================
            try { Directory.Delete(tempDir, true); } catch { }
            try { File.Delete(zipFile); } catch { }

            // ===========================================================
            // 7. 重启 LiteMonitor
            // ===========================================================
            RestartMain(baseDir);
        }
        
        // ------------------ 判断 LiteMonitor.exe（忽略大小写） ------------------
        private static bool ContainsLiteMonitorExe(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                            .Any(f => Path.GetFileName(f)
                                .Equals(ExeName, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------ 自动检测主程序目录 ------------------
        private static string? GetMainProgramDirectory(string resourcesDir)
        {
            // 先检查 resourcesDir 的上级目录
            DirectoryInfo? current = new DirectoryInfo(resourcesDir).Parent;

            if (current != null && ContainsLiteMonitorExe(current.FullName))
                return current.FullName;

            // 再检查当前目录（便携版）
            if (ContainsLiteMonitorExe(resourcesDir))
                return resourcesDir;

            return null;
        }

        // ------------------ 处理 Zip 最外层目录 ------------------
        private static string ResolveZipRoot(string tempDir)
        {
            var entries = Directory.GetFileSystemEntries(tempDir);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
                return entries[0];
            return tempDir;
        }
        //重启主程序
        private static void RestartMain(string baseDir)
        {
            // ★★★ [新增] 创建更新成功标志文件 ★★★
            try 
            {
                string tokenPath = Path.Combine(baseDir, "update_success");
                File.Create(tokenPath).Close(); // 创建并立即关闭释放句柄
            }
            catch { /* 忽略无法创建标志的错误，不影响启动 */ }

            // 原有启动逻辑
            string exePath = Path.Combine(baseDir, ExeName);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static void WaitExit(string name)
        {
            for (int i = 0; i < 80; i++)
            {
                if (Process.GetProcessesByName(name).Length == 0)
                    return;
                Thread.Sleep(200);
            }
        }

        private static void LogError(string dir, string msg)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "update_error.log"),
                    DateTime.Now + "\n" + msg);
            }
            catch { }
        }
    }
}
