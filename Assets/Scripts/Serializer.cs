using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;


namespace BenScr.UnityStack
{
    /*
     * Used for saving and loading data in JSON and binary formats
     */

    public static class Json
    {
        private const int StreamBufferSize = 64 * 1024;
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

        public static void Serialize<T>(string path, T obj, bool compress = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A valid file path is required.", nameof(path));

            string json = JsonUtility.ToJson(obj);
            WriteAtomic(path, json, compress);
        }

        public static Task SerializeAsync<T>(string path, T obj, bool compress = false)
        {
            return Task.Run(() => Serialize(path, obj, compress));
        }

        public static void SerializeList<T>(string path, IReadOnlyList<T> values)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            string json = "[" + string.Join(",", values.Select(value => JsonUtility.ToJson(value))) + "]";
            WriteAtomic(path, json, compress: false);
        }

        public static bool TryDeserializeList<T>(string path, out List<T> values, out string error)
        {
            values = null;
            error = null;

            if (!File.Exists(path))
            {
                values = new List<T>();
                return true;
            }

            try
            {
                string json = ReadAllTextAuto(path);
                string wrappedJson = $"{{\"Items\":{json}}}";
                JsonListWrapper<T> wrapper = JsonUtility.FromJson<JsonListWrapper<T>>(wrappedJson);
                values = wrapper?.Items ?? new List<T>();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not read JSON list {path}: {ex.Message}";
                return false;
            }
        }

        private static void WriteAtomic(string path, string json, bool compress)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A valid file path is required.", nameof(path));

            EnsureDir(path);
            string temporaryPath = path + ".tmp";

            try
            {
                WriteAllText(temporaryPath, json, compress);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporaryPath, path, true);
                        File.Delete(temporaryPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        public static T Deserialize<T>(string path, T defaultValue = default)
        {
            if (TryDeserialize(path, out T value, out string error))
                return value;

            Debug.LogError(error);
            return defaultValue;
        }

        public static bool TryDeserialize<T>(string path, out T value, out string error)
        {
            value = default;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "A valid file path is required.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = $"File does not exist: {path}";
                return false;
            }

            try
            {
                string json = ReadAllTextAuto(path);
                value = JsonUtility.FromJson<T>(json);

                if (value == null)
                {
                    error = $"The JSON file contains no {typeof(T).Name} data: {path}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not read {path}: {ex.Message}";
                return false;
            }
        }

        public static T DeserializeFromJson<T>(string json, T defaultValue = default)
        {
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError(ex.Message);
                return defaultValue;
            }
        }

        private static void EnsureDir(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static string ReadAllTextAuto(string path)
        {
            using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan);

            bool isGZip = fileStream.Length >= 2 &&
                          fileStream.ReadByte() == 0x1f &&
                          fileStream.ReadByte() == 0x8b;
            fileStream.Position = 0;

            if (isGZip)
            {
                using var gzipStream = new GZipStream(
                    fileStream,
                    CompressionMode.Decompress,
                    leaveOpen: false);
                using var gzipReader = new StreamReader(
                    gzipStream,
                    Utf8WithoutBom,
                    detectEncodingFromByteOrderMarks: true,
                    StreamBufferSize,
                    leaveOpen: false);
                return gzipReader.ReadToEnd();
            }

            using var reader = new StreamReader(
                fileStream,
                Utf8WithoutBom,
                detectEncodingFromByteOrderMarks: true,
                StreamBufferSize,
                leaveOpen: false);
            return reader.ReadToEnd();
        }

        private static void WriteAllText(string path, string json, bool compress)
        {
            if (!compress)
            {
                File.WriteAllText(path, json, Utf8WithoutBom);
                return;
            }

            using var fileStream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                StreamBufferSize,
                FileOptions.SequentialScan);
            using var gzipStream = new GZipStream(
                fileStream,
                System.IO.Compression.CompressionLevel.Fastest,
                leaveOpen: false);
            using var writer = new StreamWriter(
                gzipStream,
                Utf8WithoutBom,
                StreamBufferSize,
                leaveOpen: false);
            writer.Write(json);
        }

        [Serializable]
        private sealed class JsonListWrapper<T>
        {
            public List<T> Items = new();
        }
    }

    public static class Binary
    {
        public static void Serialize<T>(string path, T[] data) where T : unmanaged
        {
            string dirPath = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
                Directory.CreateDirectory(dirPath);

            if (data == null || data.Length == 0)
            {
                using var emptyFs = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                return;
            }

            ReadOnlySpan<T> span = data;
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);

            using var fs = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);

            fs.Write(bytes);
        }

        public static T[] Deserialize<T>(string path) where T : unmanaged
        {
            if (!File.Exists(path))
                return Array.Empty<T>();

            try
            {
                byte[] fileBytes = File.ReadAllBytes(path);
                if (fileBytes.Length == 0)
                    return Array.Empty<T>();

                int elementSize = Unsafe.SizeOf<T>();

                if (fileBytes.Length % elementSize != 0)
                {
                    int usableBytes = fileBytes.Length / elementSize * elementSize;
                    if (usableBytes == 0)
                        return Array.Empty<T>();

                    fileBytes = fileBytes.AsSpan(0, usableBytes).ToArray();
                }

                int count = fileBytes.Length / elementSize;
                T[] result = new T[count];

                Span<byte> byteSpan = fileBytes;
                Span<T> resultSpan = result;

                MemoryMarshal.Cast<byte, T>(byteSpan).CopyTo(resultSpan);

                return result;
            }
            catch (Exception ex)
            {
                Debug.Log(ex.Message);
                return Array.Empty<T>();
            }
        }
    }
}
