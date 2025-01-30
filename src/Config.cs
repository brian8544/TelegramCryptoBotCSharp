using System;
using System.Collections.Generic;
using System.IO;

namespace CryptoTelegramBot
{
    public class ConfigReader
    {
        public static Dictionary<string, string> ReadConfig(string filePath)
        {
            var config = new Dictionary<string, string>();

            foreach (var line in File.ReadLines(filePath))
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#") || !trimmedLine.Contains("=")) continue;

                var keyValue = trimmedLine.Split(new[] { '=' }, 2);
                if (keyValue.Length == 2)
                    config[keyValue[0].Trim()] = keyValue[1].Trim();
            }

            return config;
        }
    }
}