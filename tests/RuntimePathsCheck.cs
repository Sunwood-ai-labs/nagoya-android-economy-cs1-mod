using System;
using System.IO;
using NagoyaRoadDelivery;

internal static class RuntimePathsCheck
{
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static void Main()
    {
        const string key = "NAGOYA_PATHS_TEST_ONLY";
        Environment.SetEnvironmentVariable(key, null);
        Require(RuntimePaths.ResolveRoot(key, @"H:\CS1-PLATEAU\nagoya-station") == @"H:\CS1-PLATEAU\nagoya-station", "Legacy fallback changed");
        Environment.SetEnvironmentVariable(key, @"D:\City Data\Nagoya");
        Require(RuntimePaths.ResolveRoot(key, @"H:\unused") == @"D:\City Data\Nagoya", "Configured path was not used");
        foreach (string invalid in new string[] { "relative", @"D:relative", @"\rooted", @"\\server\share", @" D:\data" })
        {
            Environment.SetEnvironmentVariable(key, invalid);
            bool rejected = false;
            try { RuntimePaths.ResolveRoot(key, @"H:\unused"); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Invalid root accepted: " + invalid);
        }
        Environment.SetEnvironmentVariable(key, null);
        Require(RuntimePaths.Data("citywide-live-v1") == Path.Combine(RuntimePaths.DataRoot, "citywide-live-v1"), "Data child path mismatch");
        Console.WriteLine("Runtime paths: legacy fallback, configured root and invalid roots passed.");
    }
}
