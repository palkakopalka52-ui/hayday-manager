using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace FarmDesk
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--self-test")
                return SmokeTests.Run(Path.GetFullPath(args[1]));
            string folder = AppDomain.CurrentDomain.BaseDirectory;
            string key;
            using (SHA256 sha = SHA256.Create())
                key = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))
                    .Replace('/', '_').Replace('+', '_').TrimEnd('=');
            bool first;
            using (var mutex = new Mutex(true, "Local\\FarmDesk-" + key, out first))
            {
                if (!first)
                {
                    MessageBox.Show("Менеджер для этой папки уже открыт.", "Hay Day · Фермы", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 1;
                }
                try
                {
                    var store = new DataStore(Path.Combine(folder, "data"));
                    ManagerSettings settings = store.Load();
                    var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    return application.Run(new MainWindow(BlueStacksService.Discover(), store, settings));
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, "Ошибка запуска менеджера", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
