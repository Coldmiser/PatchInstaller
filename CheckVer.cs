using Serilog;
using System.Reflection;

namespace PatchInstaller
{

    public class CheckVersion()
    {
        public void Run()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "n/a";

            Log.Information("Version: {Version}", version);

            // your other code here...
        }

    /*
        LogAssemblyInfo();

        Log.Information("Version: {Version}",
            version);
        string tempPath = Path.Combine(Path.GetTempPath(), "myfile.txt");

        // With HttpClient (string)
        string text = await client.GetStringAsync("https://raw.githubusercontent.com/Coldmiser/PatchInstaller/refs/heads/master/version.txt");
//        File.WriteAllText(tempPath, text);
        Log.Information("DL-Version: {Version}",
            text);

        // your code here
    */
    }
}
