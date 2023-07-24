namespace NAPS2.App.Tests.Targets;

public class WinNet462AppTestTarget : IAppTestTarget
{
    public AppTestExe Console => GetAppTestExe("NAPS2.App.Console", "NAPS2.Console.exe", null);
    public AppTestExe Gui => GetAppTestExe("NAPS2.App.WinForms", "PrincipleScanManager.exe", null);
    public AppTestExe Worker => GetAppTestExe("NAPS2.App.Worker", "NAPS2.Worker.exe", "lib");

    private AppTestExe GetAppTestExe(string project, string exeName, string testRootSubPath)
    {
        return new AppTestExe(
            Path.Combine(AppTestHelper.SolutionRoot, project, "bin", "Debug", "net462"),
            exeName,
            TestRootSubPath: testRootSubPath);
    }

    public override string ToString() => "Windows (net462)";
}