using Microsoft.UI.Xaml;
using System;
using System.Threading.Tasks;
using CelesteGallery.Services;

namespace CelesteGallery
{
    /// <summary>
    /// 应用程序入口。
    ///
    /// 这个文件里混进了不少日志代码，是为了排查「窗口不出现、也不报错」这类问题 ——
    /// WinUI3 在启动阶段崩溃时是完全没有界面的，只有日志能说明崩在哪一步。
    /// 每一句日志都对应启动流程上的一个"关卡"。
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// 当前唯一的主窗口。
        ///
        /// 全屏、文件选择框这些事都需要窗口实例，而页面（Page）本身拿不到它所属的窗口，
        /// 所以在窗口创建时把引用记在这里，页面要用就来取。
        /// </summary>
        public static MainWindow? Instance { get; set; }

        /// <summary>
        /// 启动时命令行里带的图片路径（双击图片文件打开就是这个场景）。
        ///
        /// 记在这里是为了让浏览页知道"这次启动是来看某张图的"，
        /// 从而跳过自动加载"图片"文件夹那一步 —— 否则白读一个目录。
        /// </summary>
        public static string? StartFilePath { get; set; }

        public App()
        {
            // 第一件事就开日志：能写下这行，就说明 CLR 起来了、磁盘可写。
            // 如果日志里连这行都没有，问题在更早的地方（运行时/驱动/杀软）。
            StartupLog.BeginSession("App 开始构造");

            // 先挂线程级兜底，再碰任何 XAML —— 顺序很重要
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                StartupLog.Write("【致命】AppDomain 未处理异常", e.ExceptionObject as Exception
                    ?? new Exception(e.ExceptionObject?.ToString() ?? "未知"));

            TaskScheduler.UnobservedTaskException += (_, e) =>
                StartupLog.Write("【警告】未观察的任务异常", e.Exception);

            try
            {
                StartupLog.Write("开始 InitializeComponent()（加载 App.xaml）");
                InitializeComponent();
                StartupLog.Write("InitializeComponent() 完成");
            }
            catch (Exception ex)
            {
                StartupLog.Write("【致命】App.xaml 初始化失败", ex);
                throw;
            }

            // UI 线程上的异常（XAML 解析、控件创建等）
            UnhandledException += OnAppUnhandled;

            StartupLog.Write("App 构造结束，等待 OnLaunched");
        }

        private void OnAppUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            StartupLog.Write("【致命】UI 线程未处理异常", e.Exception);
            // 不设 e.Handled = true：让它照常崩，
            // 否则程序会带着坏掉的状态继续跑，问题更难查
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            StartupLog.Write("OnLaunched 进入，开始创建主窗口");

            try
            {
                _window = new MainWindow();
                StartupLog.Write("主窗口已创建，准备 Activate");
                _window.Activate();
                StartupLog.Write("主窗口已显示 —— 启动流程走完");

                // 启动后顺手查一次更新（CelesteMusicPlayer 同款机制）。
                //
                // 刻意等 5 秒：启动头几秒在扫目录、建缩略图，别抢资源；
                // 刻意 fire-and-forget + 吞异常：查更新失败对用户毫无感知必要，
                // 结果存在 UpdateChecker.LatestAvailable 里，打开「关于」时直接展示。
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(5000);
                        UpdateChecker.UpdateInfo? found = await UpdateChecker.CheckForUpdateAsync();
                        StartupLog.Write(found is null
                            ? "自动检查更新：无新版本（或网络不通）"
                            : $"自动检查更新：发现新版本 {found.Tag}");
                    }
                    catch (Exception ex)
                    {
                        StartupLog.Write("自动检查更新失败（不影响使用）", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                StartupLog.Write("【致命】创建主窗口失败", ex);
                throw;
            }
        }
    }
}
