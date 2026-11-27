namespace AirPrinter.Services;

public static class AppPaths
{
    public static string Root
    {
        get
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AirPrinter");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
