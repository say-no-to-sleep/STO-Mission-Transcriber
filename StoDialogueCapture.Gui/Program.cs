namespace StoDialogueCapture.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(argument => argument.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                ApplicationConfiguration.Initialize();
                UiSelfTests.Run();
                Environment.ExitCode = 0;
            }
            catch
            {
                Environment.ExitCode = 1;
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
