using System;
using System.IO;

namespace NagoyaRoadDelivery
{
    // Resolve once: a level must not mix roots after it has loaded command files.
    internal static class RuntimePaths
    {
        internal static readonly string DataRoot = ResolveRoot("NAGOYA_DATA_ROOT", @"H:\CS1-PLATEAU\nagoya-station");
        internal static readonly string DemoRoot = ResolveRoot("NAGOYA_ANDROID_DEMO_ROOT", @"H:\CS1-PLATEAU\android");

        internal static string Data(string relative)
        {
            return Path.Combine(DataRoot, relative);
        }

        internal static string ResolveRoot(string variable, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(value)) value = fallback;
            if (value != value.Trim() || !Path.IsPathRooted(value) ||
                value.Length < 3 || value[1] != ':' ||
                (value[2] != '\\' && value[2] != '/'))
                throw new ArgumentException(variable + " must be an absolute local drive path (for example D:\\NagoyaData).");
            return Path.GetFullPath(value);
        }
    }
}
