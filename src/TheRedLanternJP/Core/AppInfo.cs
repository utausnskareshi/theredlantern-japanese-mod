using System.Reflection;

namespace TheRedLanternJP.Core
{
    internal static class AppInfo
    {
        public const string Title = "The Red Lantern 日本語化パッチ（非公式）";

        public static string Version
        {
            get
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "0.0.0" : version.ToString(3);
            }
        }
    }
}
