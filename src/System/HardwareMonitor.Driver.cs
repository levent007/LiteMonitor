using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Debug = System.Diagnostics.Debug;

namespace LiteMonitor.src.SystemServices
{
    public sealed partial class HardwareMonitor
    {
        // 定义三个下载镜像地址 (请替换为您实际可用的国内镜像/Gitee直链)
        private readonly string[] _driverUrls = new[]
        {
            // 镜像 1: 官方 GitHub (国内可能慢或不通)
            "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/assets/PawnIO_setup.exe",
            
            // 镜像 2: (建议替换为您的 Gitee 发行版附件直链)
            "https://litemonitor.cn/update/PawnIO_setup.exe", 
            
            // 镜像 3: (建议替换为备用网盘或 CDN)
            "https://github.com/Diorser/LiteMonitor/raw/master/resources/assets/PawnIO_setup.exe" 
        };

        // 手动下载页面 (当自动安装失败时打开此网页让用户自己下)
        private const string ManualDownloadPage = "https://gitee.com/Diorser/LiteMonitor/raw/master/resources/assets/PawnIO_setup.exe";

        /// <summary>
        /// [独立逻辑] 智能检查并修复驱动环境
        /// </summary>
        private async Task SmartCheckDriver()
        {
            // 1. 如果配置没开 CPU，不需要检查
            if (!_cfg.IsAnyEnabled("CPU")) return;

            // 2. 优先检查注册表
            bool isDriverInstalled = IsPawnIOInstalled();

            // 3. 检查 CPU 对象有效性
            var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            bool isCpuValid = cpu != null && cpu.Sensors.Length > 0;

            // 4. 决策：如果驱动缺失
            if (!isDriverInstalled || !isCpuValid)
            {
                // 如果是完全没装，尝试静默修复
                if (!isDriverInstalled)
                {
                    Debug.WriteLine("[Driver] Driver missing. Attempting silent install...");
                    
                    bool installed = await SilentDownloadAndInstall();
                    
                    if (installed)
                    {
                        Debug.WriteLine("[Driver] Installed successfully. Reloading Computer...");
                        try 
                        {
                            _computer.Close();
                            _computer.Open();
                        }
                        catch { }
                    }
                }
            }
        }

        /// <summary>
        /// 检查注册表判断是否已安装
        /// </summary>
        private bool IsPawnIOInstalled()
        {
            try
            {
                string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
                using var k1 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(keyPath);
                if (k1 != null) return true;

                using var k2 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(keyPath);
                if (k2 != null) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 多镜像静默下载并安装
        /// </summary>
        private async Task<bool> SilentDownloadAndInstall()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "LiteMonitor_Driver.exe");
            bool downloadSuccess = false;

            // ================================================================
            // 阶段 1: 尝试下载 (轮询所有镜像)
            // ================================================================
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(15); // 每个镜像最多等 15 秒
                client.DefaultRequestHeaders.Add("User-Agent", "LiteMonitor-AutoUpdater");

                foreach (var url in _driverUrls)
                {
                    if (string.IsNullOrWhiteSpace(url) || url.Contains("example.com")) continue; // 跳过无效配置

                    try
                    {
                        Debug.WriteLine($"[Driver] Trying download from: {url}");
                        var data = await client.GetByteArrayAsync(url);
                        await File.WriteAllBytesAsync(tempPath, data);
                        
                        // 简单的文件校验 (防止下载到空文件或 404 页面)
                        if (new FileInfo(tempPath).Length > 1024) 
                        {
                            downloadSuccess = true;
                            Debug.WriteLine("[Driver] Download success.");
                            break; // 下载成功，跳出循环
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Driver] Download failed from {url}: {ex.Message}");
                    }
                }
            }

            // 如果所有镜像都挂了 -> 弹窗提示手动下载
            if (!downloadSuccess)
            {
                ShowManualFailDialog("无法连接到PawnIO驱动下载服务器 (所有镜像均尝试失败)。");
                return false;
            }

            // ================================================================
            // 阶段 2: 尝试安装
            // ================================================================
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "-install -silent", // 静默参数
                    UseShellExecute = true,
                    Verb = "runas", // 请求管理员权限
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    
                    // 清理文件
                    try { File.Delete(tempPath); } catch { }

                    if (proc.ExitCode == 0) return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Driver] Install execution failed: {ex.Message}");
                // 用户点击了 UAC 的“否”也会进这里
            }

            // 如果执行到这里，说明安装失败了 -> 弹窗提示
            ShowManualFailDialog("PawnIO驱动下载成功，但自动安装失败 (可能是权限不足或被杀毒软件拦截)。");
            return false;
        }

        /// <summary>
        /// 失败时的用户引导弹窗
        /// </summary>
        private void ShowManualFailDialog(string reason)
        {
            // 必须切换到 UI 线程显示，否则可能在后台被吞掉
            // 如果是在 Form 的构造函数 Task.Run 里调用的，MessageBox 是安全的
            var result = MessageBox.Show(
                $"PawnIO驱动缺失！\n\nLiteMonitor 无法下载安装 CPU 监控所需的PawnIO驱动。\n\n原因：{reason}\n\n点击“确定”打开下载页面，请手动下载并安装 PawnIO 驱动。",
                "驱动安装失败",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (result == DialogResult.OK)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(ManualDownloadPage) { UseShellExecute = true });
                }
                catch { }
            }
        }
    }
}