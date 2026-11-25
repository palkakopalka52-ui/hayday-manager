using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace FarmDesk
{
    public sealed class FarmOptions
    {
        public int Batch { get; set; }
        public int Price { get; set; }
        public int Reserve { get; set; }
        public int WaitIntervalSeconds { get; set; }
        public bool FreeAdvertisement { get; set; }

        public FarmOptions()
        {
            Batch = 6;
            Price = 12;
            Reserve = 6;
            WaitIntervalSeconds = 8;
            FreeAdvertisement = true;
        }

        public FarmOptions Copy()
        {
            return (FarmOptions)MemberwiseClone();
        }

        public void Validate(int plotCount)
        {
            if (Batch < 1 || Batch > 10) throw new InvalidOperationException("В партии должно быть от 1 до 10 единиц.");
            if (Price < 1 || Price > 999) throw new InvalidOperationException("Цена должна быть от 1 до 999 монет.");
            if (Reserve < plotCount || Reserve > 9999)
                throw new InvalidOperationException("Запас должен покрывать хотя бы один посев выбранных грядок.");
            if (WaitIntervalSeconds < 2 || WaitIntervalSeconds > 30)
                throw new InvalidOperationException("Интервал проверки должен быть от 2 до 30 секунд.");
        }
    }

    public sealed class FarmCard
    {
        public int Number { get; set; }
        public string InstanceId { get; set; }
        public string CreatedUtc { get; set; }
        public string Comment { get; set; }
        public bool IsTestFarm { get; set; }
        public bool RepeatUntilStopped { get; set; }
        public FarmOptions Options { get; set; }
        public string LastOperation { get; set; }
        public string LastDetail { get; set; }
        public string LastUpdateUtc { get; set; }
        public string LastScreenshot { get; set; }

        public FarmCard()
        {
            Options = new FarmOptions();
        }

        public DateTime CreatedLocal
        {
            get
            {
                DateTime parsed;
                return DateTime.TryParse(CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)
                    ? parsed.ToLocalTime() : DateTime.MinValue;
            }
        }
    }

    public sealed class ManagerSettings
    {
        public int Version { get; set; }
        public int ParallelLimit { get; set; }
        public List<FarmCard> Cards { get; set; }

        public ManagerSettings()
        {
            Version = 1;
            ParallelLimit = 1;
            Cards = new List<FarmCard>();
        }

        public FarmCard Attach(string instanceId)
        {
            DataStore.CheckId(instanceId);
            FarmCard existing = Cards.FirstOrDefault(card => String.Equals(card.InstanceId, instanceId, StringComparison.Ordinal));
            if (existing != null) return existing;
            var added = new FarmCard
            {
                InstanceId = instanceId,
                Number = Cards.Select(card => card.Number).DefaultIfEmpty(0).Max() + 1,
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Comment = "",
                LastOperation = "Подключено"
            };
            Cards.Add(added);
            return added;
        }
    }

    internal sealed class DataStore
    {
        public readonly string Root;
        private readonly object sync = new object();

        public DataStore(string root)
        {
            Root = Path.GetFullPath(root);
        }

        internal static void CheckId(string id)
        {
            if (String.IsNullOrEmpty(id) || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,80}$"))
                throw new ArgumentException("Не удалось определить идентификатор экземпляра BlueStacks.");
        }

        public ManagerSettings Load()
        {
            string file = Path.Combine(Root, "cards.json");
            ManagerSettings result = Read<ManagerSettings>(file);
            if (result == null) return new ManagerSettings();
            if (result.Version != 1) throw new InvalidDataException("Версия файла карточек не поддерживается.");
            if (result.Cards == null) result.Cards = new List<FarmCard>();
            result.ParallelLimit = Math.Max(1, Math.Min(8, result.ParallelLimit));
            foreach (FarmCard card in result.Cards)
            {
                CheckId(card.InstanceId);
                if (card.Options == null) card.Options = new FarmOptions();
            }
            if (result.Cards.Select(card => card.InstanceId).Distinct(StringComparer.Ordinal).Count() != result.Cards.Count)
                throw new InvalidDataException("В файле карточек есть повторные идентификаторы устройств.");
            return result;
        }

        public void Save(ManagerSettings value)
        {
            Write(Path.Combine(Root, "cards.json"), value);
        }

        public string DeviceFolder(string id)
        {
            CheckId(id);
            return Path.Combine(Root, "devices", id);
        }

        public string SaveScreenshot(string id, Bitmap bitmap)
        {
            string folder = Path.Combine(DeviceFolder(id), "screenshots");
            Directory.CreateDirectory(folder);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png";
            string path = Path.Combine(folder, name);
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }

        public T Read<T>(string path) where T : class
        {
            lock (sync)
            {
                if (!File.Exists(path)) return null;
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        return (T)Serializer<T>().ReadObject(stream);
                }
                catch (Exception error)
                {
                    throw new InvalidDataException("Не удалось прочитать " + path, error);
                }
            }
        }

        public void Write<T>(string path, T value)
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        Serializer<T>().WriteObject(stream, value);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        private static DataContractJsonSerializer Serializer<T>()
        {
            return new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
        }
    }

    internal sealed class CycleResult
    {
        public int Sown, Harvested, Replanted, Listed, CoinsCollected;
        public int CollectedSales, Listings;
        public bool Advertised;
        public int RemainingStock;
        public TimeSpan Duration;
        public string FinalStatus;

        public string Describe()
        {
            return String.Format(CultureInfo.GetCultureInfo("ru-RU"),
                "Посев {0} · сбор {1} · повторный посев {2} · на продаже {3} · запас {4} · монеты {5} · {6:0.0} с",
                Sown, Harvested, Replanted, Listed, RemainingStock, CoinsCollected, Duration.TotalSeconds);
        }
    }

    internal sealed class CycleStop : Exception
    {
        public CycleStop(string message) : base(message) { }
    }
}
