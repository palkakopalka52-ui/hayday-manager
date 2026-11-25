using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FarmDesk
{
    internal sealed class DeviceInfo
    {
        public readonly string Id;
        public string Name { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int Fps { get; internal set; }
        public int RamMiB { get; internal set; }
        public int Cores { get; internal set; }
        public int Dpi { get; internal set; }
        internal int ConfiguredAdbPort;
        internal int RuntimeAdbPort;

        public int AdbPort
        {
            get { return RuntimeAdbPort > 0 ? RuntimeAdbPort : ConfiguredAdbPort; }
        }

        internal DeviceInfo(string id)
        {
            Id = id;
            Name = id;
        }
    }

    internal sealed class PlayerInstance
    {
        public string InstanceId { get; internal set; }
        public readonly int ProcessId;
        public readonly IntPtr Handle;
        public readonly string Title;
        internal readonly long StartedAt;

        internal PlayerInstance(string instanceId, int processId, IntPtr handle, string title, long startedAt)
        {
            InstanceId = instanceId;
            ProcessId = processId;
            Handle = handle;
            Title = title;
            StartedAt = startedAt;
        }
    }

    internal sealed class BlueStacksService
    {
        private const string GamePackage = "com.supercell.hayday";
        private const string RegistryPath = @"SOFTWARE\BlueStacks_nxt";
        private const int AdbTimeoutMs = 7000;

        private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly Regex DeviceSetting = new Regex(
            @"^bst\.instance\.([A-Za-z0-9][A-Za-z0-9_-]{0,63})\.(display_name|fb_width|fb_height|max_fps|ram|cpus|dpi|status\.adb_port|adb_port)\s*=\s*(.*)$");

        public readonly string PlayerPath;
        public readonly string AdbPath;
        public readonly string ManagerPath;
        public readonly string ConfigPath;
        public readonly string Version;

        private readonly object sync = new object();
        private readonly Dictionary<int, PlayerInstance> startedPlayers = new Dictionary<int, PlayerInstance>();

        private BlueStacksService(string installDir, string configPath)
        {
            PlayerPath = FindFile(installDir, "HD-Player.exe");
            AdbPath = FindFile(installDir, "HD-Adb.exe");
            ManagerPath = FindFile(installDir, "HD-MultiInstanceManager.exe");
            ConfigPath = configPath;
            Version = PlayerPath == null ? "" : FileVersionInfo.GetVersionInfo(PlayerPath).FileVersion ?? "";
        }

        public static BlueStacksService Discover()
        {
            string installDir = null;
            string configPath = null;
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = hive.OpenSubKey(RegistryPath))
                {
                    if (key == null)
                        continue;

                    var candidate = key.GetValue("InstallDir") as string;
                    if (installDir == null && FindFile(candidate, "HD-Player.exe") != null)
                        installDir = candidate;

                    if (configPath == null)
                    {
                        var data = key.GetValue("DataDir") as string;
                        var parent = string.IsNullOrWhiteSpace(data) ? null : Path.GetDirectoryName(data.TrimEnd('\\', '/'));
                        configPath = FindFile(data, "bluestacks.conf") ?? FindFile(parent, "bluestacks.conf");
                    }
                }
            }

            if (installDir == null)
            {
                foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    var candidate = Path.Combine(Environment.GetFolderPath(folder), "BlueStacks_nxt");
                    if (FindFile(candidate, "HD-Player.exe") != null)
                    {
                        installDir = candidate;
                        break;
                    }
                }
            }

            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            configPath = configPath ?? FindFile(Path.Combine(common, "BlueStacks_nxt"), "bluestacks.conf");
            return new BlueStacksService(installDir, configPath);
        }

        public List<DeviceInfo> ReadDevices()
        {
            var devices = new Dictionary<string, DeviceInfo>(IdComparer);
            if (ConfigPath == null || !File.Exists(ConfigPath))
                return new List<DeviceInfo>();

            using (var file = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var setting = DeviceSetting.Match(line);
                    if (!setting.Success)
                        continue;

                    var id = setting.Groups[1].Value;
                    DeviceInfo device;
                    if (!devices.TryGetValue(id, out device))
                    {
                        device = new DeviceInfo(id);
                        devices.Add(id, device);
                    }

                    var value = setting.Groups[3].Value.Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                        value = value.Substring(1, value.Length - 2);

                    int number;
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        number = 0;

                    switch (setting.Groups[2].Value)
                    {
                        case "display_name":
                            device.Name = string.IsNullOrWhiteSpace(value) ? device.Id : value;
                            break;
                        case "fb_width": device.Width = number; break;
                        case "fb_height": device.Height = number; break;
                        case "max_fps": device.Fps = number; break;
                        case "ram": device.RamMiB = number; break;
                        case "cpus": device.Cores = number; break;
                        case "dpi": device.Dpi = number; break;
                        case "adb_port": device.ConfiguredAdbPort = number <= 65535 ? number : 0; break;
                        case "status.adb_port": device.RuntimeAdbPort = number <= 65535 ? number : 0; break;
                    }
                }
            }

            return devices.Values.OrderBy(device => device.Id, IdComparer).ToList();
        }

        public List<PlayerInstance> ReadPlayers()
        {
            lock (sync)
                return ReadPlayers(ReadDevices());
        }

        public Process Start(string instanceId)
        {
            lock (sync)
            {
                var devices = ReadDevices();
                var device = RequireDevice(devices, instanceId);
                var players = ReadPlayers(devices);
                var active = FindPlayer(players, device.Id);
                if (active != null)
                    return VerifyProcess(active);

                RequireNoUnknownPlayer(players);
                return StartPlayer(device.Id, false, true);
            }
        }

        public void OpenManager()
        {
            if (ManagerPath == null)
                throw new InvalidOperationException("Менеджер экземпляров BlueStacks не найден.");

            using (StartApplication(ManagerPath, "")) { }
        }

        public bool RequestClose(string instanceId)
        {
            lock (sync)
            {
                var devices = ReadDevices();
                var device = RequireDevice(devices, instanceId);
                var active = FindPlayer(ReadPlayers(devices), device.Id);
                if (active == null || active.Handle == IntPtr.Zero)
                    return false;

                using (var process = VerifyProcess(active))
                {
                    return process.MainWindowHandle == active.Handle && process.CloseMainWindow();
                }
            }
        }

        public void OpenGame(string instanceId)
        {
            lock (sync)
            {
                var devices = ReadDevices();
                var device = RequireDevice(devices, instanceId);
                var players = ReadPlayers(devices);
                var active = FindPlayer(players, device.Id);
                if (active == null)
                    RequireNoUnknownPlayer(players);

                using (StartPlayer(device.Id, true, active == null)) { }
            }
        }

        public string Inspect(string instanceId, CancellationToken token)
        {
            if (AdbPath == null)
                return "HD-Adb.exe не найден.";

            DeviceInfo device;
            lock (sync)
            {
                var devices = ReadDevices();
                device = RequireDevice(devices, instanceId);
                var active = ReadPlayers(devices);
                if (FindPlayer(active, device.Id) == null)
                    return "Сведения Android доступны после запуска экземпляра.";
                if (device.AdbPort < 2 || active.Any(player => player.InstanceId == null)
                    || devices.Any(item => item.Id != device.Id && item.AdbPort == device.AdbPort
                       && active.Any(player => player.InstanceId == item.Id)))
                    return "Порт ADB не удалось однозначно связать с экземпляром.";
            }

            try
            {
                var port = device.AdbPort.ToString(CultureInfo.InvariantCulture);
                var console = "emulator-" + (device.AdbPort - 1).ToString(CultureInfo.InvariantCulture);
                var serials = new[] { console, "127.0.0.1:" + port, "localhost:" + port };
                var matches = new List<string>();
                foreach (var line in RunAdb("devices -l", token).Split('\n'))
                {
                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1] == "device" && serials.Contains(parts[0]))
                        matches.Add(parts[0]);
                }

                if (matches.Count != 1)
                    return "Транспорт ADB этого экземпляра недоступен или неоднозначен.";

                var prefix = "-s " + matches[0] + " shell ";
                var api = RunAdb(prefix + "getprop ro.build.version.sdk", token).Trim();
                var result = new List<string>();
                if (Regex.IsMatch(api, @"^\d{1,3}$"))
                    result.Add("Android API " + api);

                var dump = RunAdb(prefix + "dumpsys package " + GamePackage, token);
                var version = MatchValue(dump, @"(?m)^\s*versionName=([A-Za-z0-9._+-]{1,36})\s*$");
                var code = MatchValue(dump, @"(?m)^\s*versionCode=(\d{1,12})\b");
                var minSdk = MatchValue(dump, @"(?m)^\s*versionCode=\d+\s+minSdk=(\d{1,3})\b");
                var abi = MatchValue(dump, @"(?m)^\s*primaryCpuAbi=([A-Za-z0-9_-]{1,24})\s*$");
                if (version != null)
                    result.Add("Hay Day " + version + (code == null ? "" : " (" + code + ")"));
                if (minSdk != null)
                    result.Add("minSdk " + minSdk);
                if (abi != null && abi != "null")
                    result.Add(abi);
                if (version == null)
                    result.Add(dump.IndexOf("Unable to find package", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Hay Day не установлена" : "версия Hay Day недоступна");

                return string.Join(" · ", result);
            }
            catch (IOException)
            {
                return "ADB не вернул сведения Android.";
            }
            catch (Win32Exception)
            {
                return "Не удалось запустить HD-Adb.exe.";
            }
            catch (TimeoutException)
            {
                return "Истекло время ожидания ответа ADB.";
            }
        }

        private List<PlayerInstance> ReadPlayers(List<DeviceInfo> devices)
        {
            var result = new List<PlayerInstance>();
            var processes = Process.GetProcessesByName("HD-Player");
            try
            {
                var native = processes.Length == 0 ? new Dictionary<int, NativeProcess>() : ReadNativeProcesses();
                foreach (var process in processes)
                {
                    var pid = process.Id;
                    long startedAt = ReadStartTime(process);
                    IntPtr handle;
                    string title;
                    try
                    {
                        if (process.HasExited)
                            continue;
                        handle = process.MainWindowHandle;
                        title = process.MainWindowTitle;
                    }
                    catch (InvalidOperationException) { continue; }
                    catch (Win32Exception)
                    {
                        handle = IntPtr.Zero;
                        title = "";
                    }

                    NativeProcess inspection;
                    native.TryGetValue(pid, out inspection);
                    var instanceId = ResolveInstance(devices, pid, startedAt, title, inspection, processes.Length == 1);
                    result.Add(new PlayerInstance(instanceId, pid, handle, title, startedAt));
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }

            var duplicates = result.Where(player => player.InstanceId != null)
                .GroupBy(player => player.InstanceId, IdComparer).Where(group => group.Count() > 1);
            foreach (var group in duplicates)
            {
                foreach (var player in group)
                    player.InstanceId = null;
            }

            var alive = new HashSet<int>(result.Select(player => player.ProcessId));
            foreach (var pid in startedPlayers.Keys.Where(pid => !alive.Contains(pid)).ToArray())
                startedPlayers.Remove(pid);

            return result;
        }

        private string ResolveInstance(List<DeviceInfo> devices, int pid, long startedAt, string title, NativeProcess native, bool onlyPlayer)
        {
            if (native != null && !string.IsNullOrEmpty(native.Path) && PlayerPath != null && !IdComparer.Equals(native.Path, PlayerPath))
                return null;

            if (native != null && native.InstanceId != null)
            {
                var fromArgs = devices.FirstOrDefault(device => IdComparer.Equals(device.Id, native.InstanceId));
                return fromArgs == null ? null : fromArgs.Id;
            }

            PlayerInstance started;
            if (startedAt != 0 && startedPlayers.TryGetValue(pid, out started) && started.StartedAt == startedAt)
            {
                var fromLaunch = devices.FirstOrDefault(device => IdComparer.Equals(device.Id, started.InstanceId));
                return fromLaunch == null ? null : fromLaunch.Id;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                var byTitle = devices.Where(device => IdComparer.Equals(device.Name, title)).ToList();
                if (byTitle.Count == 1)
                    return byTitle[0].Id;
            }

            return onlyPlayer && devices.Count == 1 ? devices[0].Id : null;
        }

        private static Dictionary<int, NativeProcess> ReadNativeProcesses()
        {
            var result = new Dictionary<int, NativeProcess>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\cimv2");
                var query = new ObjectQuery("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'HD-Player.exe'");
                var options = new EnumerationOptions { Timeout = TimeSpan.FromMilliseconds(700), ReturnImmediately = true };
                using (var search = new ManagementObjectSearcher(scope, query, options))
                using (var processes = search.Get())
                {
                    foreach (ManagementBaseObject process in processes)
                    {
                        using (process)
                        {
                            var pid = Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture);
                            var path = process["ExecutablePath"] as string;
                            var line = process["CommandLine"] as string;
                            result[pid] = new NativeProcess(path, ParseInstanceArgument(line));
                        }
                    }
                }
            }
            catch (ManagementException) { }
            catch (COMException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }

            return result;
        }

        private static string ParseInstanceArgument(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return null;

            int count;
            var argv = CommandLineToArgvW(commandLine, out count);
            if (argv == IntPtr.Zero)
                return null;

            try
            {
                for (var index = 1; index < count; index++)
                {
                    var arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size));
                    if (IdComparer.Equals(arg, "--instance"))
                    {
                        if (index + 1 == count)
                            return "";
                        return Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, (index + 1) * IntPtr.Size));
                    }
                    if (arg != null && arg.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
                        return arg.Substring("--instance=".Length);
                }

                return null;
            }
            finally { LocalFree(argv); }
        }

        private Process StartPlayer(string instanceId, bool openGame, bool remember)
        {
            if (PlayerPath == null)
                throw new InvalidOperationException("BlueStacks App Player не найден.");

            var arguments = "--instance " + instanceId;
            if (openGame)
                arguments += " --cmd launchApp --package \"" + GamePackage + "\" --source desktop_shortcut";

            var process = StartApplication(PlayerPath, arguments);
            if (remember)
            {
                var startedAt = ReadStartTime(process);
                if (startedAt != 0)
                    startedPlayers[process.Id] = new PlayerInstance(instanceId, process.Id, IntPtr.Zero, "", startedAt);
            }

            return process;
        }

        private static Process StartApplication(string executable, string arguments)
        {
            var start = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true
            };
            var process = Process.Start(start);
            if (process == null)
                throw new InvalidOperationException("Windows не вернула процесс приложения.");
            return process;
        }

        private string RunAdb(string arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(AdbPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(start))
            {
                if (process == null)
                    throw new IOException("HD-Adb.exe не запустился.");

                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    while (!process.WaitForExit(100))
                    {
                        token.ThrowIfCancellationRequested();
                        if (stopwatch.ElapsedMilliseconds >= AdbTimeoutMs)
                            throw new TimeoutException();
                    }

                    var remaining = Math.Max(0, AdbTimeoutMs - (int)stopwatch.ElapsedMilliseconds);
                    if (!Task.WaitAll(new Task[] { output, error }, remaining, token))
                        throw new TimeoutException();
                    if (process.ExitCode != 0)
                        throw new IOException("HD-Adb.exe вернул ошибку.");
                    return output.Result;
                }
                catch (AggregateException exception)
                {
                    throw new IOException("Не удалось прочитать ответ HD-Adb.exe.", exception);
                }
                finally
                {
                    // Only the short-lived ADB client belongs to this request.
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
        }

        private static Process VerifyProcess(PlayerInstance player)
        {
            var process = Process.GetProcessById(player.ProcessId);
            try
            {
                if (!IdComparer.Equals(process.ProcessName, "HD-Player") || process.HasExited ||
                    (player.StartedAt != 0 && ReadStartTime(process) != player.StartedAt))
                    throw new InvalidOperationException("Процесс экземпляра BlueStacks уже сменился.");
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        private static long ReadStartTime(Process process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks; }
            catch (Win32Exception) { return 0; }
            catch (InvalidOperationException) { return 0; }
        }

        private static DeviceInfo RequireDevice(IEnumerable<DeviceInfo> devices, string instanceId)
        {
            var device = devices.FirstOrDefault(item => IdComparer.Equals(item.Id, instanceId));
            if (device == null)
                throw new InvalidOperationException("Экземпляр отсутствует в конфигурации BlueStacks.");
            return device;
        }

        private static PlayerInstance FindPlayer(IEnumerable<PlayerInstance> players, string instanceId)
        {
            return players.FirstOrDefault(player => IdComparer.Equals(player.InstanceId, instanceId));
        }

        private static void RequireNoUnknownPlayer(IEnumerable<PlayerInstance> players)
        {
            if (players.Any(player => player.InstanceId == null))
                throw new InvalidOperationException("Не удалось связать запущенное окно BlueStacks с экземпляром.");
        }

        private static string MatchValue(string text, string pattern)
        {
            var match = Regex.Match(text, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string FindFile(string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return null;
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        private sealed class NativeProcess
        {
            public readonly string Path;
            public readonly string InstanceId;

            public NativeProcess(string path, string instanceId)
            {
                Path = path;
                InstanceId = instanceId;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
