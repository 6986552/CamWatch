namespace CamWatch;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // 未处理异常只记日志，不弹窗（弹窗会卡住关闭）
        Application.ThreadException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "camwatch_err.txt"), DateTime.Now + " " + e.Exception + "\n"); } catch { }
        };

        bool smokeTest = args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(smokeTest));
    }
}
