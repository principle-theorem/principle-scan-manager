using NAPS2.EntryPoints;

namespace NAPS2;

static class Program
{
    /// <summary>
    /// The PrincipleScanManager.exe main method.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // Use reflection to avoid antivirus false positives (yes, really)
        typeof(WinFormsEntryPoint).GetMethod("Run").Invoke(null, new object[] { args });
    }
}